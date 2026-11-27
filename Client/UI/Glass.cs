using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteScreen.Client.UI;

/// <summary>Poloprůhledné „sklo“ v návrhu C: rozmazaná tapeta plochy pod oknem + tmavý nádech
/// (stejný princip jako Mica ve Windows 11). Pozadí se posouvá s oknem.</summary>
internal static class GlassBackdrop
{
    private static Bitmap? _blurred;
    private static bool _loaded;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, uint param, System.Text.StringBuilder value, uint winIni);

    /// <summary>Malá (rozmazaná) verze tapety – při zvětšení se sama „rozmaže“.</summary>
    private static Bitmap? Blurred()
    {
        if (_loaded) return _blurred;
        _loaded = true;
        try
        {
            var path = new System.Text.StringBuilder(520);
            SystemParametersInfo(0x73 /* SPI_GETDESKWALLPAPER */, (uint)path.Capacity, path, 0);
            string file = path.ToString();
            if (!File.Exists(file))
                file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            if (!File.Exists(file)) return null;

            using var ms = new MemoryStream(File.ReadAllBytes(file));
            using var img = Image.FromStream(ms);
            int w = Math.Max(8, img.Width / 28), h = Math.Max(8, img.Height / 28);
            var small = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new Rectangle(0, 0, w, h));
            }
            _blurred = small;
        }
        catch { _blurred = null; }
        return _blurred;
    }

    /// <summary>Nakreslí sklo do klientské oblasti okna.</summary>
    public static void Paint(Graphics g, Control owner, Rectangle area)
    {
        var bmp = Blurred();
        var screen = Screen.FromControl(owner).Bounds;
        var origin = owner.PointToScreen(Point.Empty);
        if (bmp != null)
        {
            // tapeta „vyplnit“ – pokrýt celou obrazovku se zachováním poměru stran
            double s = Math.Max((double)screen.Width / bmp.Width, (double)screen.Height / bmp.Height);
            int w = (int)Math.Ceiling(bmp.Width * s), h = (int)Math.Ceiling(bmp.Height * s);
            int x = screen.X + (screen.Width - w) / 2 - origin.X;
            int y = screen.Y + (screen.Height - h) / 2 - origin.Y;
            var state = g.Save();
            g.SetClip(area);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attr = new ImageAttributes();
            attr.SetWrapMode(WrapMode.TileFlipXY); // bez tmavých okrajů při zvětšení
            g.DrawImage(bmp, new Rectangle(x, y, w, h), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attr);
            g.Restore(state);
            using var tint = new SolidBrush(Color.FromArgb(150, 8, 8, 10));
            g.FillRectangle(tint, area);
        }
        else
        {
            // bez tapety: tmavý přechod s oranžovým nádechem
            using var lg = new LinearGradientBrush(new Rectangle(-origin.X, -origin.Y, Math.Max(1, screen.Width), Math.Max(1, screen.Height)),
                Color.FromArgb(34, 24, 20), Color.FromArgb(12, 12, 16), 60f);
            g.FillRectangle(lg, area);
        }
    }
}

/// <summary>Formulář s poloprůhledným pozadím. Podřízené prvky s průhledným pozadím ho prosvítají.</summary>
public class GlassForm : Form
{
    public GlassForm()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) => GlassBackdrop.Paint(e.Graphics, this, ClientRectangle);

    // pozadí závisí na poloze okna – po přesunu překreslit
    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Invalidate(true);
    }
}

/// <summary>Dlaždice počítače v přihlašovacím okně (ikona, název, stav).</summary>
public sealed class DeviceTile : Control
{
    private bool _hover, _selected;

    public DeviceTile()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = Theme.BodyBold;
        Size = new Size(190, 88);
        TabStop = true;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Address { get; set; } = "";
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Subtitle { get; set; } = "";
    /// <summary>Podtitulek oranžově (např. „spí · probudit“).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SubtitleAccent { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Online { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Icon { get; set; } = Glyph.Desktop;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill = _selected ? Theme.AccentA(36) : Color.FromArgb(_hover ? 22 : 13, 255, 255, 255);
        Color border = _selected ? Theme.AccentA(140) : Color.FromArgb(_hover ? 50 : 26, 255, 255, 255);
        Theme.FillRound(g, fill, r, 16);
        Theme.DrawRound(g, border, r, 16, _selected ? 1.5f : 1f);

        Theme.DrawGlyph(g, Icon, 16f, _selected ? Theme.AccentText : Theme.TextDim, new Rectangle(12, 10, 30, 30));
        if (Online)
        {
            using var dot = new SolidBrush(Theme.Success);
            g.FillEllipse(dot, Width - 22, 16, 8, 8);
        }
        TextRenderer.DrawText(g, Text, Theme.BodyBold, new Rectangle(14, 44, Width - 24, 20), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, Subtitle, Theme.Small, new Rectangle(14, 64, Width - 24, 18),
            SubtitleAccent ? Theme.AccentText : Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, RectangleF.Inflate(r, -2, -2), 14, 1.2f);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

/// <summary>Plovoucí panel vlevo nad obrazem (Soubory, Terminál…): poloprůhledné sklo – rozmazaný
/// výřez obrazu pod panelem se ztmavením, zaoblené rohy a záhlaví.</summary>
public sealed class SidePanel : Panel
{
    private readonly FlatButton _close = new() { Glyph = Glyph.Close, Kind = ButtonKind.Ghost, Height = 30 };
    private readonly Font _titleFont = new(Theme.BodyBold.FontFamily, 11f, FontStyle.Bold);
    // rozmazané pozadí se mění jen pozvolna – stačí ho obnovit jednou za čas (méně překreslování)
    private readonly System.Windows.Forms.Timer _glassTimer = new() { Interval = 1200 };
    private readonly HashSet<Control> _hooked = new();
    private string _title = "";
    private Bitmap? _backdrop;
    private int _seenVersion = int.MinValue;
    private Size _seenSize;

    public Panel Body { get; } = new() { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 14, 14), BackColor = Color.Transparent };

    /// <summary>Zdroj pozadí: rozmazaný výřez obrazu pro danou oblast (souřadnice okna).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Rectangle, Bitmap?>? BackdropSource { get; set; }

    /// <summary>Verze obrazu – pozadí se obnoví jen po změně.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<int>? BackdropVersion { get; set; }

    public event Action? Closed;

    public SidePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Padding = new Padding(1, 50, 1, 1);
        Controls.Add(Body);
        Controls.Add(_close);
        _close.Click += (_, _) => Closed?.Invoke();
        new ToolTip().SetToolTip(_close, "Zavřít panel");
        _glassTimer.Tick += (_, _) => RefreshGlass(force: false);
        _glassTimer.Start();
    }

    /// <summary>WS_EX_COMPOSITED: panel i všechny prvky v něm se kreslí najednou do jedné vyrovnávací paměti.
    /// Bez toho se při obnově průhledného pozadí na okamžik ukáže napůl překreslený panel (problikávání).</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get => _title; set { _title = value; Invalidate(new Rectangle(0, 0, Width, 50)); } }

    /// <summary>Vloží stránku do panelu a pohlídá posouvání (průhledné pozadí se musí po posunu překreslit).</summary>
    public void ShowPage(Control page)
    {
        SuspendLayout();
        Body.Controls.Clear();
        Body.Controls.Add(page);
        ResumeLayout();
        Hook(page);
        RefreshGlass(force: true);
    }

    private void Hook(Control c)
    {
        if (_hooked.Add(c) && c is ScrollableControl sc)
        {
            sc.Scroll += (_, _) => InvalidateGlass(sc);
            sc.MouseWheel += (_, _) => InvalidateGlass(sc);
        }
        foreach (Control child in c.Controls) Hook(child);
        c.ControlAdded += (_, e) => { if (e.Control != null) Hook(e.Control); };
    }

    /// <summary>Nové pozadí, když se obraz pod panelem změnil.</summary>
    private void RefreshGlass(bool force)
    {
        if (!Visible || BackdropSource == null) return;
        int v = BackdropVersion?.Invoke() ?? 0;
        if (!force && v == _seenVersion && Size == _seenSize && _backdrop != null) return;
        _seenVersion = v;
        _seenSize = Size;
        var bmp = BackdropSource(Bounds);
        var old = _backdrop;
        _backdrop = bmp;
        old?.Dispose();
        InvalidateGlass(this);
    }

    /// <summary>Překreslí panel a průhledné prvky v něm (ne seznamy a textová pole – ty nemají průhledné pozadí).</summary>
    private static void InvalidateGlass(Control c)
    {
        c.Invalidate(false);
        foreach (Control child in c.Controls)
            if (child.BackColor.A < 255 || child is Card || child is Panel || child is AccentSwatch || child is InputBox)
                InvalidateGlass(child);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) RefreshGlass(force: true);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        RefreshGlass(force: true);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _close.Location = new Point(Width - _close.Width - 12, 10);
        // zaoblené rohy – oblast okna podle tvaru panelu
        using var path = Theme.RoundRect(new RectangleF(0, 0, Width, Height), 18);
        Region = new Region(path);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_backdrop != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var attr = new ImageAttributes();
            attr.SetWrapMode(WrapMode.TileFlipXY); // bez tmavých okrajů při zvětšení
            g.DrawImage(_backdrop, ClientRectangle, 0, 0, _backdrop.Width, _backdrop.Height, GraphicsUnit.Pixel, attr);
            using var tint = new SolidBrush(Color.FromArgb(196, 20, 20, 24)); // ~77 % – text zůstane čitelný
            g.FillRectangle(tint, ClientRectangle);
        }
        else g.Clear(Theme.Panel);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Theme.DrawRound(g, Color.FromArgb(34, 255, 255, 255), new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 18);
        TextRenderer.DrawText(g, _title, _titleFont, new Rectangle(18, 10, Width - 70, 30), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _glassTimer.Dispose();
            _titleFont.Dispose();
            _backdrop?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Kolečko s barvou zvýraznění (výběr v panelu Obraz).</summary>
public sealed class AccentSwatch : Control
{
    private bool _hover;

    public AccentSwatch(string name, Color color)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Swatch = color;
        AccessibleName = name;
        AccessibleRole = AccessibleRole.RadioButton;
        Size = new Size(40, 40);
        Margin = new Padding(3, 3, 6, 3);
        Cursor = Cursors.Hand;
        TabStop = true;
        new ToolTip().SetToolTip(this, name);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Swatch { get; }

    private bool IsCurrent => Theme.Accent.ToArgb() == Swatch.ToArgb();

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Parent != null) ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
        else g.Clear(Theme.Panel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool current = IsCurrent;
        if (current || _hover || (Focused && ShowFocusCues))
        {
            using var ring = new Pen(current ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(90, 255, 255, 255), 2f);
            g.DrawEllipse(ring, 2, 2, Width - 5, Height - 5);
        }
        using var fill = new SolidBrush(Swatch);
        g.FillEllipse(fill, 7, 7, Width - 15, Height - 15);
    }
}
