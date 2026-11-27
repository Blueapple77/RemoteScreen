using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Snímání obrazovky přes CoreGraphics a kódování do JPEG přes ImageIO. Bez externích knihoven.</summary>
internal static class ScreenCapture
{
    private static readonly IntPtr JpegUti = Native.CFStringCreateWithCString(IntPtr.Zero, "public.jpeg", Native.kCFStringEncodingUTF8);
    private static readonly object Gate = new();
    private static bool _nativeFailed;

    public static DisplayInfo[] GetDisplays()
    {
        Native.CGGetActiveDisplayList(0, null, out uint count);
        if (count == 0) count = 1;
        var ids = new uint[count];
        Native.CGGetActiveDisplayList(count, ids, out count);
        uint main = Native.CGMainDisplayID();

        var list = new List<DisplayInfo>();
        for (int i = 0; i < count; i++)
        {
            var b = Native.CGDisplayBounds(ids[i]);
            list.Add(new DisplayInfo(i, ids[i], b.Origin.X, b.Origin.Y, b.Size.Width, b.Size.Height, ids[i] == main));
        }
        if (list.Count == 0)
        {
            var b = Native.CGDisplayBounds(main);
            list.Add(new DisplayInfo(0, main, b.Origin.X, b.Origin.Y, b.Size.Width, b.Size.Height, true));
        }
        return list.ToArray();
    }

    /// <summary>Nasnímá displej a vrátí JPEG. Při selhání nativního API přejde na nástroj screencapture.</summary>
    public static byte[]? CaptureJpeg(DisplayInfo display, int maxWidth, int quality)
    {
        if (!_nativeFailed)
        {
            var data = CaptureNative(display.Id, maxWidth, quality);
            if (data != null) return data;
            _nativeFailed = true;
            Log.Warn("CGDisplayCreateImage nevrátil obraz (chybí oprávnění Nahrávání obrazovky, nebo ho tahle verze macOS nepodporuje). Přecházím na 'screencapture' – bude to pomalejší.");
        }
        return CaptureViaCli(display.Index);
    }

    // ---------------- surové snímky (režim „jen změny“) ----------------

    /// <summary>Nasnímá displej do surových pixelů BGRX (zmenšené na nejvýš maxWidth). false = nejde.</summary>
    public static unsafe bool CaptureRaw(DisplayInfo display, int maxWidth, ref RawFrame? frame)
    {
        if (_nativeFailed) return false;
        IntPtr image = Native.CGDisplayCreateImage(display.Id);
        if (image == IntPtr.Zero) return false;
        try
        {
            int w = (int)Native.CGImageGetWidth(image), h = (int)Native.CGImageGetHeight(image);
            int tw = w, th = h;
            if (maxWidth > 0 && w > maxWidth)
            {
                tw = maxWidth;
                th = Math.Max(1, (int)Math.Round(h * (double)maxWidth / w));
            }
            if (frame == null || !frame.Matches(tw, th)) frame = new RawFrame(tw, th);
            fixed (byte* p = frame.Pixels)
            {
                IntPtr cs = Native.CGColorSpaceCreateDeviceRGB();
                // kCGImageAlphaNoneSkipFirst (6) | kCGBitmapByteOrder32Little (2 << 12) → v paměti B,G,R,X
                IntPtr ctx = Native.CGBitmapContextCreate((IntPtr)p, (nuint)tw, (nuint)th, 8, (nuint)frame.Stride, cs, 6 | (2 << 12));
                Native.CGColorSpaceRelease(cs);
                if (ctx == IntPtr.Zero) return false;
                Native.CGContextSetInterpolationQuality(ctx, tw == w ? 1 : 4); // bez zmenšení žádná interpolace
                Native.CGContextDrawImage(ctx, new CGRect(0, 0, tw, th), image);
                Native.CGContextRelease(ctx);
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Native.CGImageRelease(image);
        }
    }

    /// <summary>JPEG z obdélníku surového snímku.</summary>
    public static unsafe byte[]? EncodeRegion(RawFrame f, int x, int y, int w, int h, int quality)
    {
        fixed (byte* p = f.Pixels)
        {
            IntPtr cs = Native.CGColorSpaceCreateDeviceRGB();
            IntPtr ctx = Native.CGBitmapContextCreate((IntPtr)(p + y * f.Stride + x * 4), (nuint)w, (nuint)h, 8, (nuint)f.Stride, cs, 6 | (2 << 12));
            Native.CGColorSpaceRelease(cs);
            if (ctx == IntPtr.Zero) return null;
            IntPtr img = Native.CGBitmapContextCreateImage(ctx); // kopie pixelů
            Native.CGContextRelease(ctx);
            if (img == IntPtr.Zero) return null;
            try { return EncodeJpeg(img, quality); }
            finally { Native.CGImageRelease(img); }
        }
    }

    private static byte[]? CaptureNative(uint displayId, int maxWidth, int quality)
    {
        IntPtr image = Native.CGDisplayCreateImage(displayId);
        if (image == IntPtr.Zero) return null;

        IntPtr scaled = IntPtr.Zero;
        try
        {
            int w = (int)Native.CGImageGetWidth(image);
            int h = (int)Native.CGImageGetHeight(image);
            IntPtr toEncode = image;

            if (maxWidth > 0 && w > maxWidth)
            {
                int tw = maxWidth;
                int th = Math.Max(1, (int)Math.Round(h * (double)maxWidth / w));
                scaled = Scale(image, tw, th);
                if (scaled != IntPtr.Zero) toEncode = scaled;
            }
            return EncodeJpeg(toEncode, quality);
        }
        finally
        {
            if (scaled != IntPtr.Zero) Native.CGImageRelease(scaled);
            Native.CGImageRelease(image);
        }
    }

    private static IntPtr Scale(IntPtr image, int width, int height)
    {
        IntPtr cs = Native.CGColorSpaceCreateDeviceRGB();
        // kCGImageAlphaNoneSkipFirst (6) | kCGBitmapByteOrder32Little (2 << 12)
        IntPtr ctx = Native.CGBitmapContextCreate(IntPtr.Zero, (nuint)width, (nuint)height, 8, 0, cs, 6 | (2 << 12));
        Native.CGColorSpaceRelease(cs);
        if (ctx == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            Native.CGContextSetInterpolationQuality(ctx, 4); // medium
            Native.CGContextDrawImage(ctx, new CGRect(0, 0, width, height), image);
            return Native.CGBitmapContextCreateImage(ctx);
        }
        finally
        {
            Native.CGContextRelease(ctx);
        }
    }

    private static byte[]? EncodeJpeg(IntPtr image, int quality)
    {
        IntPtr data = Native.CFDataCreateMutable(IntPtr.Zero, 0);
        IntPtr dest = IntPtr.Zero, number = IntPtr.Zero, props = IntPtr.Zero;
        try
        {
            dest = Native.CGImageDestinationCreateWithData(data, JpegUti, 1, IntPtr.Zero);
            if (dest == IntPtr.Zero) return null;

            double q = Math.Clamp(quality, 10, 100) / 100.0;
            number = Native.CFNumberCreate(IntPtr.Zero, Native.kCFNumberFloat64Type, ref q);
            lock (Gate) props = Native.CreateDictionary(Native.JpegQualityKey, number);

            Native.CGImageDestinationAddImage(dest, image, props);
            if (!Native.CGImageDestinationFinalize(dest)) return null;

            int len = (int)Native.CFDataGetLength(data);
            var result = new byte[len];
            Marshal.Copy(Native.CFDataGetBytePtr(data), result, 0, len);
            return result;
        }
        finally
        {
            if (props != IntPtr.Zero) Native.CFRelease(props);
            if (number != IntPtr.Zero) Native.CFRelease(number);
            if (dest != IntPtr.Zero) Native.CFRelease(dest);
            Native.CFRelease(data);
        }
    }

    private static byte[]? CaptureViaCli(int displayIndex)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"remotescreen_{Environment.ProcessId}.jpg");
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/screencapture")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-x", "-t", "jpg", "-D", (displayIndex + 1).ToString(), tmp })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return null;
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            return File.Exists(tmp) ? File.ReadAllBytes(tmp) : null;
        }
        catch (Exception ex)
        {
            Log.Warn("screencapture selhal: " + ex.Message);
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }
}
