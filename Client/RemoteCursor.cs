using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace RemoteScreen.Client;

/// <summary>Kurzor nad obrazem Macu: skutečný tvar z Macu přepočtený na měřítko obrazu,
/// do té doby šipka v macOS stylu.</summary>
internal sealed class RemoteCursor : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] private static extern bool DestroyCursor(IntPtr hCursor);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    private Bitmap? _shape;          // originál z Macu
    private float _hotX, _hotY, _wPt, _hPt;
    private IntPtr _handle;
    private int _lastSize = -1;

    public Cursor Current { get; private set; }

    public RemoteCursor()
    {
        Current = BuildDefaultArrow(1f);
    }

    /// <summary>Nový tvar z Macu (převezme bitmapu).</summary>
    public void SetShape(Bitmap png, float hotX, float hotY, float widthPt, float heightPt)
    {
        _shape?.Dispose();
        _shape = png;
        _hotX = hotX; _hotY = hotY;
        _wPt = widthPt > 0 ? widthPt : png.Width;
        _hPt = heightPt > 0 ? heightPt : png.Height;
        _lastSize = -1;
    }

    /// <summary>Přepočte kurzor pro měřítko obrazu (pixely okna na 1 bod Macu). Vrací true, když se změnil.</summary>
    public bool Update(float scale)
    {
        scale = Math.Clamp(scale, 0.35f, 2.5f);
        if (_shape == null)
        {
            int s = (int)Math.Round(24 * Math.Max(scale, 1f));
            if (s == _lastSize) return false;
            _lastSize = s;
            Replace(BuildDefaultArrow(Math.Max(scale, 1f)));
            return true;
        }

        // kurzor nechat aspoň tak velký, aby byl dobře vidět
        float k = Math.Max(scale, 1.0f);
        int w = Math.Clamp((int)Math.Round(_wPt * k), 12, 128);
        int h = Math.Clamp((int)Math.Round(_hPt * k), 12, 128);
        if (w == _lastSize) return false;
        _lastSize = w;

        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_shape, new Rectangle(0, 0, w, h));
        }
        int hx = (int)Math.Round(_hotX * w / _wPt), hy = (int)Math.Round(_hotY * h / _hPt);
        Replace(Create(bmp, Math.Clamp(hx, 0, w - 1), Math.Clamp(hy, 0, h - 1)));
        return true;
    }

    private void Replace(Cursor c)
    {
        IntPtr old = _handle;
        Current = c;
        _handle = c.Handle;
        if (old != IntPtr.Zero) DestroyCursor(old);
    }

    private static Cursor Create(Bitmap bmp, int hotX, int hotY)
    {
        IntPtr icon = bmp.GetHicon();
        try
        {
            GetIconInfo(icon, out var ii);
            ii.fIcon = false;
            ii.xHotspot = hotX;
            ii.yHotspot = hotY;
            IntPtr cur = CreateIconIndirect(ref ii);
            DeleteObject(ii.hbmMask);
            DeleteObject(ii.hbmColor);
            return new Cursor(cur);
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    /// <summary>Černá šipka s bílým okrajem (macOS styl).</summary>
    private Cursor BuildDefaultArrow(float scale)
    {
        int size = (int)Math.Round(24 * scale);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(scale, scale);
            var pts = new PointF[]
            {
                new(2, 1.5f), new(2, 18.5f), new(6.2f, 14.6f), new(9, 20.8f), new(11.6f, 19.7f), new(8.9f, 13.6f), new(14.6f, 13.6f),
            };
            using var path = new GraphicsPath();
            path.AddPolygon(pts);
            using (var outline = new Pen(Color.White, 2.6f) { LineJoin = LineJoin.Round })
                g.DrawPath(outline, path);
            using (var fill = new SolidBrush(Color.Black))
                g.FillPath(fill, path);
        }
        IntPtr old = _handle;
        var c = Create(bmp, (int)Math.Round(2 * scale), (int)Math.Round(1.5f * scale));
        if (old == IntPtr.Zero) _handle = c.Handle;
        return c;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) DestroyCursor(_handle);
        _handle = IntPtr.Zero;
        _shape?.Dispose();
    }
}
