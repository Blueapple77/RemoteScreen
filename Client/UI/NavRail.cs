using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace RemoteScreen.Client.UI;

/// <summary>Svislá navigační lišta vlevo (ikona + popisek).</summary>
public sealed class NavRail : Control
{
    private readonly List<(string Glyph, string Text)> _items = new();
    private int _selected, _hover = -1;
    private const int ItemsTop = 72, ItemHeight = 66;

    public event EventHandler? SelectedIndexChanged;

    public NavRail()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Nav;
        Width = 84;
        Dock = DockStyle.Left;
        Cursor = Cursors.Hand;
    }

    public void Add(string glyph, string text)
    {
        _items.Add((glyph, text));
        Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < 0 || value >= _items.Count || value == _selected) return;
            _selected = value;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int HitTest(Point p)
    {
        if (p.Y < ItemsTop) return -1;
        int i = (p.Y - ItemsTop) / ItemHeight;
        return i < _items.Count ? i : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = HitTest(e.Location);
        if (h != _hover) { _hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int h = HitTest(e.Location);
        if (h >= 0) SelectedIndex = h;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // logo
        var logo = new RectangleF((Width - 40) / 2f, 18, 40, 40);
        using (var lg = new LinearGradientBrush(logo, Color.FromArgb(64, 156, 255), Color.FromArgb(88, 70, 230), 90f))
        using (var path = Theme.RoundRect(logo, 10))
            g.FillPath(lg, path);
        Theme.DrawGlyph(g, Glyph.Desktop, 15f, Color.White, Rectangle.Round(logo));

        for (int i = 0; i < _items.Count; i++)
        {
            var r = new Rectangle(8, ItemsTop + i * ItemHeight + 4, Width - 16, ItemHeight - 8);
            bool sel = i == _selected;
            if (sel) Theme.FillRound(g, Theme.Dark ? Theme.SurfaceHover : Theme.Surface, r, 8);
            else if (i == _hover) Theme.FillRound(g, Theme.Dark ? Theme.Surface : Theme.SurfaceHover, r, 8);
            if (sel) Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y + 16, 3, r.Height - 32), 1.5f);

            Color c = sel ? Theme.Accent : Theme.TextDim;
            Theme.DrawGlyph(g, _items[i].Glyph, 16f, c, new Rectangle(r.X, r.Y + 6, r.Width, 28));
            TextRenderer.DrawText(g, _items[i].Text, Theme.Small, new Rectangle(r.X, r.Y + 34, r.Width, 18),
                sel ? Theme.Text : Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }

        using var border = new Pen(Theme.Border);
        g.DrawLine(border, Width - 1, 0, Width - 1, Height);
    }
}
