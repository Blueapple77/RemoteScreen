using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace RemoteScreen.Client.UI;

public enum ButtonKind { Primary, Secondary, Ghost, Danger }

/// <summary>Plochý zaoblený button s ikonou.</summary>
public sealed class FlatButton : Control
{
    private bool _hover, _down;
    private string _glyph = "";
    private ButtonKind _kind = ButtonKind.Secondary;
    private bool _autoSize = true;

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable |
                 ControlStyles.StandardClick, true);
        SetStyle(ControlStyles.StandardDoubleClick, false);
        Height = 34;
        BackColor = Color.Transparent;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        TabStop = true;
        Margin = new Padding(3);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get => _glyph; set { _glyph = value ?? ""; Fit(); Invalidate(); } }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonKind Kind { get => _kind; set { _kind = value; Invalidate(); } }

    /// <summary>Zvýraznění aktivního stavu (např. zapnutý přepínač v liště).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    /// <summary>Zaoblení rohů.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float Radius { get; set; } = 10;

    /// <summary>false = pevná šířka (nastavená zvenku)</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoFit { get => _autoSize; set { _autoSize = value; Fit(); } }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

    private void Fit()
    {
        if (!_autoSize) return;
        Size = GetPreferredSize(Size.Empty);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        int w = 12;
        if (_glyph.Length > 0) w += 16;
        if (Text.Length > 0) w += (_glyph.Length > 0 ? 8 : 4) + TextRenderer.MeasureText(Text, Font).Width;
        w += Text.Length > 0 ? 10 : 6;
        return new Size(Math.Max(w, Height), Height);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; _down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _down = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        Color fill, fore, border = Color.Empty;
        switch (_kind)
        {
            case ButtonKind.Primary:
                fill = _down ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
                fore = Theme.OnAccent;
                break;
            case ButtonKind.Danger:
                fill = _down ? Theme.Blend(Theme.Danger, Color.Black, 0.15f) : _hover ? Theme.Blend(Theme.Danger, Color.White, 0.12f) : Theme.Danger;
                fore = Color.White;
                break;
            case ButtonKind.Ghost:
                fill = _down ? Theme.SurfacePressed : _hover ? Theme.SurfaceHover : Active ? Theme.AccentA(40) : Color.Transparent;
                fore = Active ? Theme.AccentText : Theme.Text;
                break;
            default:
                fill = _down ? Theme.SurfacePressed : _hover ? Theme.SurfaceHover : Theme.Surface;
                fore = Theme.Text;
                border = Theme.Border;
                break;
        }
        if (!Enabled)
        {
            fill = _kind is ButtonKind.Ghost ? Color.Transparent : Theme.Blend(fill, Theme.Bg, 0.55f);
            fore = Theme.Blend(fore, Theme.Bg, 0.5f);
        }

        if (fill.A > 0) Theme.FillRound(g, fill, r, Radius);
        if (!border.IsEmpty) Theme.DrawRound(g, border, r, Radius);
        if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, RectangleF.Inflate(r, -1.5f, -1.5f), Radius - 1, 1.5f);

        int x = Text.Length > 0 ? 12 : 0;
        if (_glyph.Length > 0)
        {
            var gr = Text.Length > 0 ? new Rectangle(x, 0, 16, Height) : ClientRectangle;
            Theme.DrawGlyph(g, _glyph, 11f, _kind == ButtonKind.Primary ? fore : Active ? Theme.AccentText : fore, gr);
            x += 16 + 8;
        }
        if (Text.Length > 0)
        {
            var tr = new Rectangle(_glyph.Length > 0 ? x : 0, 0, _glyph.Length > 0 ? Width - x - 8 : Width, Height);
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis |
                        (_glyph.Length > 0 ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, Text, Font, tr, fore, flags);
        }
    }
}

/// <summary>Přepínač ve stylu Windows 11.</summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked, _hover;

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        Height = 34;
        Margin = new Padding(6, 3, 6, 3);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Width = 40 + 10 + TextRenderer.MeasureText(Text, Font).Width + 4;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, (Height - 20) / 2f, 40, 20);
        Color on = Enabled ? (_hover ? Theme.AccentHover : Theme.Accent) : Theme.TextDim;
        if (_checked)
        {
            Theme.FillRound(g, on, track, 10);
            using var b = new SolidBrush(Theme.OnAccent);
            g.FillEllipse(b, track.Right - 17, track.Y + 3, 14, 14);
        }
        else
        {
            Theme.FillRound(g, _hover ? Theme.SurfaceHover : Theme.Input, track, 10);
            Theme.DrawRound(g, Theme.TextDim, track, 10);
            using var b = new SolidBrush(Theme.TextDim);
            g.FillEllipse(b, track.X + 4, track.Y + 4, 12, 12);
        }
        if (Focused && ShowFocusCues) Theme.DrawRound(g, Theme.Accent, RectangleF.Inflate(track, 2.5f, 2.5f), 12, 1.5f);

        TextRenderer.DrawText(g, Text, Font, new Rectangle(48, 0, Width - 48, Height),
            Enabled ? Theme.Text : Theme.TextDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Posuvník 0–100.</summary>
public sealed class Slider : Control
{
    private int _value = 50;
    private bool _drag, _hover;

    public event EventHandler? ValueChanged;
    /// <summary>Po puštění myši.</summary>
    public event EventHandler? ValueCommitted;

    public Slider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Height = 30;
        Width = 200;
        Cursor = Cursors.Hand;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (_value == value) return;
            _value = value;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetFromX(int x) => Value = (int)Math.Round((x - 9) * 100.0 / Math.Max(1, Width - 18));

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); _drag = true; SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (_drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_drag) return;
        _drag = false;
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) { Value -= 5; ValueCommitted?.Invoke(this, EventArgs.Empty); }
        if (e.KeyCode == Keys.Right) { Value += 5; ValueCommitted?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cy = Height / 2f;
        float x0 = 9, x1 = Width - 9;
        float xv = x0 + (x1 - x0) * _value / 100f;
        Theme.FillRound(g, Theme.Border, new RectangleF(x0, cy - 2, x1 - x0, 4), 2);
        Theme.FillRound(g, Theme.Accent, new RectangleF(x0, cy - 2, Math.Max(0, xv - x0), 4), 2);
        using var outer = new SolidBrush(Theme.Surface);
        using var ring = new Pen(Theme.Border);
        using var inner = new SolidBrush(Theme.Accent);
        g.FillEllipse(outer, xv - 9, cy - 9, 18, 18);
        g.DrawEllipse(ring, xv - 9, cy - 9, 18, 18);
        float ri = _drag ? 4 : _hover || Focused ? 6 : 5;
        g.FillEllipse(inner, xv - ri, cy - ri, ri * 2, ri * 2);
    }
}

/// <summary>Tenký pruh průběhu.</summary>
public sealed class ThinProgress : Control
{
    private int _value;

    public ThinProgress()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Height = 6;
        Width = 200;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value { get => _value; set { _value = Math.Clamp(value, 0, 100); Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0, (Height - 4) / 2f, Width - 1, 4);
        Theme.FillRound(g, Theme.Border, r, 2);
        Theme.FillRound(g, Theme.Accent, new RectangleF(r.X, r.Y, r.Width * _value / 100f, r.Height), 2);
    }
}

/// <summary>Karta: zaoblený panel s nadpisem a ikonou.</summary>
public class Card : Panel
{
    private string _glyph = "";

    public Card()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Padding = new Padding(16, 14, 16, 16);
        Margin = new Padding(8);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Glyph { get => _glyph; set { _glyph = value ?? ""; Invalidate(); } }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Padding = new Padding(Padding.Left, string.IsNullOrEmpty(Text) ? 14 : 50, Padding.Right, Padding.Bottom);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Parent != null) ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
        else g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, Color.FromArgb(150, BackColor), r, 14); // průsvitná – na skle panelu prosvítá obraz
        Theme.DrawRound(g, Theme.Border, r, 14);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (string.IsNullOrEmpty(Text)) return;
        int x = 16;
        if (_glyph.Length > 0)
        {
            Theme.DrawGlyph(e.Graphics, _glyph, 13f, Theme.Accent, new Rectangle(x, 14, 22, 24));
            x += 30;
        }
        TextRenderer.DrawText(e.Graphics, Text, Theme.Heading, new Rectangle(x, 12, Width - x - 12, 28), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Textové pole se zaobleným rámečkem (TextBox uvnitř).</summary>
public sealed class InputBox : Control
{
    public TextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None,
    };

    public InputBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 34;
        Width = 200;
        Margin = new Padding(3);
        Box.Font = Theme.Body;
        Box.BackColor = Theme.Input;
        Box.ForeColor = Theme.Text;
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Cursor = Cursors.IBeam;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder { get => Box.PlaceholderText; set => Box.PlaceholderText = value; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Password { get => Box.UseSystemPasswordChar; set => Box.UseSystemPasswordChar = value; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new Font Font { get => Box.Font; set { Box.Font = value; LayoutBox(); } }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Box.Focus(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Box.Focus(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutBox(); }

    private void LayoutBox()
    {
        int h = Box.PreferredHeight;
        Box.SetBounds(10, Math.Max(2, (Height - h) / 2), Math.Max(10, Width - 20), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (Parent != null) ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
        else g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRound(g, Theme.Input, r, 10);
        bool focused = Box.Focused;
        Theme.DrawRound(g, focused ? Theme.Accent : Theme.Border, r, 10, focused ? 1.5f : 1f);
    }
}

/// <summary>Popisek s barvou textu z motivu.</summary>
public sealed class TLabel : Label
{
    public TLabel(string text = "", bool dim = false)
    {
        Text = text;
        AutoSize = true;
        Font = Theme.Body;
        ForeColor = dim ? Theme.TextDim : Theme.Text;
        BackColor = Color.Transparent;
        Margin = new Padding(3, 9, 3, 3);
    }
}

internal static class UiExtensions
{
    /// <summary>Jednotný vzhled pro standardní ComboBox.</summary>
    public static T Styled<T>(this T combo) where T : ComboBox
    {
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Theme.Input;
        combo.ForeColor = Theme.Text;
        combo.Font = Theme.Body;
        combo.Margin = new Padding(3, 5, 3, 3);
        return combo;
    }

    public static Control Spacer(int width) => new Panel { Width = width, Height = 1, Margin = Padding.Empty, BackColor = Color.Transparent };

    /// <summary>Svislý oddělovač do lišty.</summary>
    public static Control Divider() => new Panel { Width = 1, Height = 24, Margin = new Padding(8, 8, 8, 0), BackColor = Theme.Border };
}
