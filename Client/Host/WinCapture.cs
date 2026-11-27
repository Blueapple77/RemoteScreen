using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteScreen.Host;
using RemoteScreen.Shared;
using static RemoteScreen.Client.Host.WinNative;

namespace RemoteScreen.Client.Host;

/// <summary>Snímání obrazovky Windows (GDI) a tvaru kurzoru.</summary>
internal static class WinCapture
{
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private static readonly object Gate = new();
    private static Bitmap? _buffer;

    public static DisplayInfo[] GetDisplays()
    {
        var screens = Screen.AllScreens;
        var list = new DisplayInfo[screens.Length];
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds; // fyzické pixely (aplikace je PerMonitorV2)
            list[i] = new DisplayInfo(i, (uint)i, b.X, b.Y, b.Width, b.Height, screens[i].Primary);
        }
        return list;
    }

    private static Bitmap? _scaled;

    /// <summary>Nasnímá displej do surových pixelů BGRX (zmenšené na nejvýš maxWidth). false = nejde.</summary>
    public static bool CaptureRaw(DisplayInfo d, int maxWidth, ref RawFrame? frame)
    {
        lock (Gate)
        {
            try
            {
                int w = (int)d.Width, h = (int)d.Height;
                if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
                {
                    _buffer?.Dispose();
                    _buffer = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                }
                using (var g = Graphics.FromImage(_buffer))
                    g.CopyFromScreen((int)d.X, (int)d.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

                Bitmap src = _buffer;
                int tw = w, th = h;
                if (maxWidth > 0 && w > maxWidth)
                {
                    tw = maxWidth;
                    th = Math.Max(1, (int)Math.Round(h * (double)maxWidth / w));
                    if (_scaled == null || _scaled.Width != tw || _scaled.Height != th)
                    {
                        _scaled?.Dispose();
                        _scaled = new Bitmap(tw, th, PixelFormat.Format32bppRgb);
                    }
                    using var g = Graphics.FromImage(_scaled);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(_buffer, new Rectangle(0, 0, tw, th));
                    src = _scaled;
                }

                if (frame == null || !frame.Matches(tw, th)) frame = new RawFrame(tw, th);
                var bd = src.LockBits(new Rectangle(0, 0, tw, th), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    for (int y = 0; y < th; y++)
                        System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, frame.Pixels, y * frame.Stride, frame.Stride);
                }
                finally
                {
                    src.UnlockBits(bd);
                }
                return true;
            }
            catch
            {
                return false; // zamčená obrazovka, UAC apod.
            }
        }
    }

    /// <summary>JPEG z obdélníku surového snímku (lze volat paralelně).</summary>
    public static unsafe byte[]? EncodeRegion(RawFrame f, int x, int y, int w, int h, int quality)
    {
        try
        {
            fixed (byte* p = f.Pixels)
            {
                using var bmp = new Bitmap(w, h, f.Stride, PixelFormat.Format32bppRgb, (IntPtr)(p + y * f.Stride + x * 4));
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
                using var ms = new MemoryStream();
                bmp.Save(ms, Jpeg, ep);
                return ms.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }

    public static byte[]? CaptureJpeg(DisplayInfo d, int maxWidth, int quality)
    {
        lock (Gate)
        {
            try
            {
                int w = (int)d.Width, h = (int)d.Height;
                if (_buffer == null || _buffer.Width != w || _buffer.Height != h)
                {
                    _buffer?.Dispose();
                    _buffer = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                }
                using (var g = Graphics.FromImage(_buffer))
                    g.CopyFromScreen((int)d.X, (int)d.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

                Bitmap toEncode = _buffer;
                Bitmap? scaled = null;
                if (maxWidth > 0 && w > maxWidth)
                {
                    int th = Math.Max(1, (int)Math.Round(h * (double)maxWidth / w));
                    scaled = new Bitmap(maxWidth, th, PixelFormat.Format24bppRgb);
                    using var g = Graphics.FromImage(scaled);
                    g.InterpolationMode = InterpolationMode.Bilinear;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(_buffer, new Rectangle(0, 0, maxWidth, th));
                    toEncode = scaled;
                }

                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
                using var ms = new MemoryStream();
                toEncode.Save(ms, Jpeg, ep);
                scaled?.Dispose();
                return ms.ToArray();
            }
            catch
            {
                // zamčená obrazovka, UAC apod. – snímek nejde pořídit
                return null;
            }
        }
    }

    /// <summary>Tvar kurzoru: vykreslí se na černé a na bílé pozadí a z rozdílu se dopočítá průhlednost
    /// (funguje i pro černobílé a „invertující“ kurzory, např. textový).</summary>
    public static CursorShapeData? GetCursorIfChanged(ref int lastHash)
    {
        var ci = new CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return null;
        int hash = ci.hCursor.GetHashCode();
        if (hash == lastHash) return null;

        if (!GetIconInfo(ci.hCursor, out var ii)) return null;
        try
        {
            int w = 32, h = 32;
            IntPtr bmpHandle = ii.hbmColor != IntPtr.Zero ? ii.hbmColor : ii.hbmMask;
            if (bmpHandle != IntPtr.Zero && GetObject(bmpHandle, System.Runtime.InteropServices.Marshal.SizeOf<BITMAP>(), out var bm) != 0)
            {
                w = bm.bmWidth;
                h = ii.hbmColor != IntPtr.Zero ? bm.bmHeight : bm.bmHeight / 2; // černobílý: maska AND + XOR pod sebou
            }
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            using var onBlack = Render(ci.hCursor, w, h, Color.Black);
            using var onWhite = Render(ci.hCursor, w, h, Color.White);
            using var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            Combine(onBlack, onWhite, result);

            using var ms = new MemoryStream();
            result.Save(ms, ImageFormat.Png);
            lastHash = hash;
            return new CursorShapeData(ms.ToArray(), ii.xHotspot, ii.yHotspot, w, h);
        }
        finally
        {
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
        }
    }

    private static Bitmap Render(IntPtr cursor, int w, int h, Color background)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(background);
        IntPtr hdc = g.GetHdc();
        try { DrawIconEx(hdc, 0, 0, cursor, w, h, 0, IntPtr.Zero, 3 /* DI_NORMAL */); }
        finally { g.ReleaseHdc(hdc); }
        return bmp;
    }

    private static unsafe void Combine(Bitmap onBlack, Bitmap onWhite, Bitmap result)
    {
        var rect = new Rectangle(0, 0, result.Width, result.Height);
        var b = onBlack.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var wt = onWhite.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var r = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < rect.Height; y++)
            {
                byte* pb = (byte*)b.Scan0 + y * b.Stride;
                byte* pw = (byte*)wt.Scan0 + y * wt.Stride;
                byte* pr = (byte*)r.Scan0 + y * r.Stride;
                for (int x = 0; x < rect.Width; x++)
                {
                    int alpha = 0;
                    for (int c = 0; c < 3; c++)
                        alpha = Math.Max(alpha, 255 - (pw[x * 4 + c] - pb[x * 4 + c]));
                    alpha = Math.Clamp(alpha, 0, 255);
                    for (int c = 0; c < 3; c++)
                        pr[x * 4 + c] = alpha == 0 ? (byte)0 : (byte)Math.Min(255, pb[x * 4 + c] * 255 / alpha);
                    pr[x * 4 + 3] = (byte)alpha;
                }
            }
        }
        finally
        {
            onBlack.UnlockBits(b);
            onWhite.UnlockBits(wt);
            result.UnlockBits(r);
        }
    }
}
