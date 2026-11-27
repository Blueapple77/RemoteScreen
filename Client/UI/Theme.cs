using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RemoteScreen.Client.UI;

/// <summary>Barvy, písma a pomocné kreslení. Tmavý/světlý motiv se řídí nastavením Windows.</summary>
internal static class Theme
{
    public static bool Dark { get; private set; }

    public static Color Panel, AccentText;

    /// <summary>Barvy zvýraznění k výběru (stejné jako v návrzích designu).</summary>
    public static readonly (string Name, Color Color)[] AccentPresets =
    {
        ("Oranžová", Color.FromArgb(255, 138, 61)),   // #FF8A3D
        ("Modrá", Color.FromArgb(91, 140, 255)),      // #5B8CFF
        ("Zelená", Color.FromArgb(61, 214, 140)),     // #3DD68C
        ("Fialová", Color.FromArgb(180, 140, 255)),   // #B48CFF
        ("Růžová", Color.FromArgb(255, 111, 168)),    // #FF6FA8
    };

    /// <summary>Nastaví barvu zvýraznění a odvodí z ní ostatní odstíny.</summary>
    public static void SetAccent(Color c)
    {
        Accent = c;
        AccentHover = Blend(c, Color.White, 0.18f);
        AccentPressed = Blend(c, Color.Black, 0.10f);
        AccentText = Blend(c, Color.White, 0.32f);  // světlejší – pro text a ikony na tmavém
        Selection = Blend(c, Color.FromArgb(20, 20, 24), 0.68f);
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        OnAccent = lum > 0.55 ? Color.FromArgb(26, 14, 5) : Color.White;
    }

    /// <summary>Akcent s průhledností (výplně a rámečky).</summary>
    public static Color AccentA(int alpha) => Color.FromArgb(alpha, Accent);

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        string h = hex.Trim().TrimStart('#');
        return h.Length == 6 && int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v)
            ? Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255) : fallback;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    public static Color Bg, Nav, Surface, SurfaceHover, SurfacePressed, Input, Border, Text, TextDim,
        Accent, AccentHover, AccentPressed, OnAccent, Danger, Success, Warning, ViewBg, Selection;

    public static Font Body = null!, BodyBold = null!, Small = null!, Heading = null!, Title = null!, Mono = null!;
    public static string IconFamily = "Segoe MDL2 Assets";
    private static readonly Dictionary<float, Font> IconFonts = new();

    public static void Init()
    {
        // návrh C: vždy tmavý vzhled s oranžovým akcentem
        Dark = true;
        Bg = Color.FromArgb(11, 11, 13);             // #0B0B0D – okno
        Panel = Color.FromArgb(20, 20, 24);          // plovoucí panely a přihlášení
        Nav = Color.FromArgb(20, 20, 24);
        Surface = Color.FromArgb(30, 30, 35);        // karty, sekundární tlačítka
        SurfaceHover = Color.FromArgb(44, 44, 50);
        SurfacePressed = Color.FromArgb(37, 37, 42);
        Input = Color.FromArgb(28, 28, 33);
        Border = Color.FromArgb(50, 50, 56);
        Text = Color.FromArgb(242, 242, 244);        // #F2F2F4
        TextDim = Color.FromArgb(169, 170, 178);     // #A9AAB2
        SetAccent(Color.FromArgb(255, 138, 61));     // #FF8A3D
        _ = IsSystemDark();
        Danger = Color.FromArgb(255, 107, 107);
        Success = Color.FromArgb(61, 214, 140);      // #3DD68C
        Warning = Color.FromArgb(240, 170, 20);
        ViewBg = Bg;

        string ui = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
        string display = FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        Body = new Font(ui, 9.75f);
        BodyBold = new Font(ui, 9.75f, FontStyle.Bold);
        Small = new Font(ui, 8.5f);
        Heading = new Font(display, 12f, FontStyle.Bold);
        Title = new Font(display, 20f, FontStyle.Bold);
        Mono = new Font(FontExists("Cascadia Mono") ? "Cascadia Mono" : "Consolas", 10f);
        IconFamily = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    public static Font Icon(float size)
    {
        lock (IconFonts)
        {
            if (!IconFonts.TryGetValue(size, out var f))
                IconFonts[size] = f = new Font(IconFamily, size);
            return f;
        }
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool FontExists(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- titulkový pruh okna ----------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Tmavý titulkový pruh (Windows 10 20H1+) a zaoblené rohy / barva titulku (Windows 11).</summary>
    public static void ApplyWindowChrome(Form form)
    {
        try
        {
            int dark = Dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int caption = ColorTranslator.ToWin32(Bg);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Win11)
            int round = 2;
            DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch { /* starší Windows */ }
    }

    // ---------------- kreslení ----------------

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
    {
        using var path = RoundRect(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    public static void DrawRound(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
    {
        using var path = RoundRect(r, radius);
        using var p = new Pen(c, width);
        g.DrawPath(p, path);
    }

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static void DrawGlyph(Graphics g, string glyph, float size, Color color, Rectangle bounds)
    {
        TextRenderer.DrawText(g, glyph, Icon(size), bounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Ikony z písma Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
internal static class Glyph
{
    public const string Desktop = "";
    public const string Terminal = "";
    public const string Folder = "";
    public const string Remote = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Stop = "";
    public const string FullScreen = "";
    public const string Download = "";
    public const string Upload = "";
    public const string Refresh = "";
    public const string Up = "";
    public const string Home = "";
    public const string NewFolder = "";
    public const string Delete = "";
    public const string Open = "";
    public const string Volume = "";
    public const string VolumeLow = "";
    public const string Mute = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Lock = "";
    public const string Power = "";
    public const string Restart = "";
    public const string Moon = "";
    public const string Brightness = "";
    public const string Keyboard = "";
    public const string Copy = "";
    public const string Paste = "";
    public const string Search = "";
    public const string Globe = "";
    public const string Message = "";
    public const string Speech = "";
    public const string Apps = "";
    public const string Cancel = "";
    public const string Connect = "";
    public const string Warning = "";
    public const string Info = "";
    public const string Check = "";
    public const string Send = "";
    public const string SignOut = "";
    public const string Close = "";
    public const string Task = "";
    public const string Settings = "\uE713";
    public const string ChevronLeft = "\uE76B";
    public const string ChevronRight = "\uE76C";
    public const string Laptop = "\uE7F8";
    public const string Clear = "";
}
