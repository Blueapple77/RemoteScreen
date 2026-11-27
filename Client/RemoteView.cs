using System.Drawing.Drawing2D;
using RemoteScreen.Client.UI;

namespace RemoteScreen.Client;

/// <summary>Položka doku dole v obraze (návrh C).</summary>
public sealed class DockItem
{
    public string Glyph = "";
    public string Label = "";
    public Action? Click;
    public Func<bool>? Active;
    public bool Danger;
    /// <summary>Před položkou svislý oddělovač.</summary>
    public bool SeparatorBefore;
    /// <summary>Myš najela na položku (např. vyžádat náhled obrazovky).</summary>
    public Action? Hover;
    /// <summary>Náhled vedle položky při najetí myší (null = žádný).</summary>
    public Func<Image?>? Preview;
    /// <summary>Popisek pod náhledem.</summary>
    public Func<string>? PreviewCaption;
}

/// <summary>Zobrazuje obraz druhého počítače (se zachováním poměru stran), převádí myš na souřadnice 0..1
/// a kreslí přes obraz plovoucí dok, stavovou pilulku a oznámení (návrh C).</summary>
public sealed class RemoteView : Control
{
    // ---------------- boční menu (vpravo, vysouvací za šipkou) a stavová pilulka ----------------

    public List<DockItem> DockItems { get; } = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            var before = StatusRect();
            _statusText = value;
            // překreslit starou i novou plochu – jinak po zkrácení textu zůstane vedle šedý zbytek
            Invalidate(Rectangle.Inflate(Rectangle.Union(before, StatusRect()), 2, 2));
        }
    }

    /// <summary>Poloha stavové bubliny přesunuté uživatelem (levý horní roh, 0–1); null = výchozí vpravo nahoře.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PointF? StatusPosition { get => _statusPos; set { _statusPos = value; Invalidate(); } }
    private PointF? _statusPos;

    /// <summary>Uživatel bublinu přesunul (nebo dvojklikem vrátil do rohu).</summary>
    public event Action? StatusMoved;

    private bool _statusDown, _statusMoved;
    private Point _statusGrab;
    private DateTime _statusLastClick;
    private Point _statusGrabOffset;
    private string _statusText = "";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool StatusOk { get; set; } = true;

    /// <summary>Menu je vysunuté.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DockOpen { get => _dockOpen; set { _dockOpen = value; Invalidate(); } }
    private bool _dockOpen = true;

    /// <summary>Svislá poloha šipky (0 = nahoře, 1 = dole); uživatel ji posouvá tažením šipky.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float DockPosition { get => _dockPos; set { _dockPos = Math.Clamp(value, 0f, 1f); Invalidate(); } }
    private float _dockPos = 0.5f;

    /// <summary>Celá obrazovka: se zavřeným menu se schová i stavová pilulka.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool IsFullscreen { get => _fullscreen; set { _fullscreen = value; Invalidate(); } }
    private bool _fullscreen;

    /// <summary>Uživatel menu vysunul/zasunul nebo posunul šipku (pro uložení do nastavení).</summary>
    public event Action? DockStateChanged;

    /// <summary>Zachováno kvůli kompatibilitě (dříve dok dole); otevřený panel menu nezavírá.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool DockPinned { get; set; }

    private const int MenuW = 100, ItemH = 54, HandleW = 20, HandleH = 60, HandleGap = 6;
    private const int HitOutside = -2, HitMenu = -1, HitHandle = -3;

    private int _itemH = ItemH; // v nízkém okně se menu zhustí
    private int _dockHover = -1, _dockPressed = -1;
    private bool _downOnDock, _handleDown, _handleMoved;
    private int _handleDownY;
    private readonly List<Rectangle> _dockRects = new();
    private Rectangle _dockRect, _handleRect;
    private Cursor? _savedCursor;
    private readonly ToolTip _dockTip = new();

    /// <summary>Bublinu ukázat vždy (např. během přenosu souborů – i na celé obrazovce se zasunutým menu).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ForceStatus { get => _forceStatus; set { if (_forceStatus != value) { _forceStatus = value; Invalidate(); } } }
    private bool _forceStatus;

    private bool StatusVisible => _statusText.Length > 0 && (_forceStatus || !(_fullscreen && !_dockOpen));

    /// <summary>Spočítá polohu šipky a menu (vpravo, svisle podle DockPosition).</summary>
    private void LayoutDock()
    {
        _dockRects.Clear();
        int seps = DockItems.Count(i => i.SeparatorBefore) - (DockItems.FirstOrDefault()?.SeparatorBefore == true ? 1 : 0);
        // menu nikdy nesmí přesáhnout obraz: když se nevejde, zmenšit položky (pod 46 px jen ikony)
        int avail = Math.Max(0, Height - 20 - 12 - seps * 9);
        _itemH = DockItems.Count > 0 ? Math.Clamp(avail / DockItems.Count, 28, ItemH) : ItemH;
        int menuH = 12 + DockItems.Count * _itemH + seps * 9;
        int cy = (int)(_dockPos * Height);
        cy = Math.Clamp(cy, HandleH / 2 + 8, Math.Max(HandleH / 2 + 8, Height - HandleH / 2 - 8));

        if (_dockOpen && DockItems.Count > 0)
        {
            int y = Math.Clamp(cy - menuH / 2, 10, Math.Max(10, Height - menuH - 10));
            _dockRect = new Rectangle(Width - MenuW - 10, y, MenuW, menuH);
            int iy = y + 6;
            for (int i = 0; i < DockItems.Count; i++)
            {
                if (DockItems[i].SeparatorBefore && i > 0) iy += 9;
                _dockRects.Add(new Rectangle(_dockRect.X + 6, iy, MenuW - 12, _itemH));
                iy += _itemH;
            }
            _handleRect = new Rectangle(_dockRect.X - HandleW - HandleGap, cy - HandleH / 2, HandleW, HandleH); // mezera mezi šipkou a menu
        }
        else
        {
            _dockRect = Rectangle.Empty;
            _handleRect = new Rectangle(Width - HandleW, cy - HandleH / 2, HandleW, HandleH);
        }
    }

    private int DockHit(Point p)
    {
        if (DockItems.Count == 0) return HitOutside;
        LayoutDock();
        if (_handleRect.Contains(p)) return HitHandle;
        if (!_dockOpen || !_dockRect.Contains(p)) return HitOutside;
        for (int i = 0; i < _dockRects.Count; i++) if (_dockRects[i].Contains(p)) return i;
        return HitMenu;
    }

    private bool IsDockHit(int hit) => hit != HitOutside;

    private Rectangle DockInvalidRect()
    {
        var r = Rectangle.Union(Rectangle.Inflate(_dockRect, 4, 4), Rectangle.Inflate(_handleRect, 4, 4));
        return _previewRect.IsEmpty ? r : Rectangle.Union(r, Rectangle.Inflate(_previewRect, 4, 4));
    }

    private Rectangle _previewRect;

    /// <summary>Náhled se změnil (dorazil nový obrázek) – překreslit.</summary>
    public void InvalidatePreview()
    {
        var before = _previewRect;
        _previewRect = PreviewRect();
        Invalidate(Rectangle.Inflate(Rectangle.Union(before.IsEmpty ? _previewRect : before, _previewRect.IsEmpty ? before : _previewRect), 4, 4));
    }

    /// <summary>Rámeček náhledu vlevo od položky, nad kterou je myš (nikdy mimo obraz).</summary>
    private Rectangle PreviewRect()
    {
        if (!_dockOpen || _dockHover < 0 || _dockHover >= DockItems.Count || _dockHover >= _dockRects.Count) return Rectangle.Empty;
        var img = DockItems[_dockHover].Preview?.Invoke();
        if (img == null || img.Width <= 0) return Rectangle.Empty;
        int w = 240, ih = Math.Clamp(w * img.Height / img.Width, 60, 180);
        int h = ih + 12 + 22;
        var item = _dockRects[_dockHover];
        int x = Math.Max(10, Math.Min(_dockRect.X, _handleRect.X) - (w + 12) - 8); // vlevo od menu i od šipky
        int y = Math.Clamp(item.Y + item.Height / 2 - h / 2, 10, Math.Max(10, Height - h - 10));
        return new Rectangle(x, y, w + 12, h);
    }

    private void PaintPreview(Graphics g)
    {
        _previewRect = PreviewRect();
        if (_previewRect.IsEmpty) return;
        var item = DockItems[_dockHover];
        var img = item.Preview!.Invoke()!;
        var r = _previewRect;
        Theme.FillRound(g, Color.FromArgb(235, 22, 22, 26), r, 14);
        Theme.DrawRound(g, Color.FromArgb(26, 255, 255, 255), r, 14);
        var ir = new Rectangle(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12 - 22);
        // zachovat poměr stran uvnitř rámečku
        float scale = Math.Min(ir.Width / (float)img.Width, ir.Height / (float)img.Height);
        int dw = (int)(img.Width * scale), dh = (int)(img.Height * scale);
        var dr = new Rectangle(ir.X + (ir.Width - dw) / 2, ir.Y + (ir.Height - dh) / 2, dw, dh);
        var old = g.InterpolationMode;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(img, dr);
        g.InterpolationMode = old;
        string caption = item.PreviewCaption?.Invoke() ?? item.Label;
        TextRenderer.DrawText(g, caption, Theme.Small, new Rectangle(r.X + 8, r.Bottom - 26, r.Width - 16, 20), Theme.TextDim,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }

    private void PaintDock(Graphics g)
    {
        if (DockItems.Count == 0) return;
        LayoutDock();

        // šipka (úchyt) – kliknutím vysunout/zasunout, tažením posunout
        var hr = new RectangleF(_handleRect.X + 0.5f, _handleRect.Y + 0.5f, _handleRect.Width - 1, _handleRect.Height - 1);
        bool hot = _dockHover == HitHandle || _handleDown;
        Theme.FillRound(g, Color.FromArgb(hot ? 235 : 205, 22, 22, 26), hr, 9);
        Theme.DrawRound(g, Color.FromArgb(hot ? 60 : 30, 255, 255, 255), hr, 9);
        Theme.DrawGlyph(g, _dockOpen ? Glyph.ChevronRight : Glyph.ChevronLeft, 9f, hot ? Theme.AccentText : Theme.TextDim, _handleRect);

        if (!_dockOpen) return;
        Theme.FillRound(g, Color.FromArgb(225, 22, 22, 26), _dockRect, 20);
        Theme.DrawRound(g, Color.FromArgb(26, 255, 255, 255), _dockRect, 20);
        for (int i = 0; i < DockItems.Count; i++)
        {
            var item = DockItems[i];
            var rc = _dockRects[i];
            if (item.SeparatorBefore && i > 0)
            {
                using var pen = new Pen(Color.FromArgb(30, 255, 255, 255));
                g.DrawLine(pen, rc.X + 14, rc.Y - 5, rc.Right - 14, rc.Y - 5);
            }
            bool active = item.Active?.Invoke() == true;
            if (active) Theme.FillRound(g, Theme.AccentA(41), rc, 12);
            else if (i == _dockHover) Theme.FillRound(g, Color.FromArgb(_dockPressed == i ? 30 : 18, 255, 255, 255), rc, 12);
            Color fore = item.Danger ? Color.FromArgb(255, 138, 128) : active ? Theme.AccentText : Theme.Text;
            if (_itemH >= 46)
            {
                Theme.DrawGlyph(g, item.Glyph, 13f, fore, new Rectangle(rc.X, rc.Y + (_itemH - 48) / 2 + 6, rc.Width, 24));
                TextRenderer.DrawText(g, item.Label, Theme.Small, new Rectangle(rc.X, rc.Y + (_itemH - 48) / 2 + 31, rc.Width, 18), fore,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            else Theme.DrawGlyph(g, item.Glyph, 12f, fore, rc); // nízké okno: jen ikona (popisek v nápovědě)
        }
    }

    private Rectangle StatusRect()
    {
        int w = Math.Min(Width - 28, TextRenderer.MeasureText(_statusText, Theme.Small).Width + 40);
        if (_statusPos is PointF sp)
        {
            int px = Math.Clamp((int)(sp.X * Width), 4, Math.Max(4, Width - w - 6));
            int py = Math.Clamp((int)(sp.Y * Height), 4, Math.Max(4, Height - 34));
            return new Rectangle(px, py, w + 2, 30);
        }
        int right = Width - 14;
        // s vysunutým menu pilulku posunout vlevo vedle něj, pokud by se překrývaly
        if (_dockOpen && DockItems.Count > 0 && !_dockRect.IsEmpty && _dockRect.Top < 12 + 30 + 6) right = _dockRect.X - 10;
        if (_dockOpen && DockItems.Count > 0 && _handleRect.Top < 12 + 30 + 6) right = Math.Min(right, _handleRect.X - 8);
        return new Rectangle(right - w, 12, w + 2, 30);
    }

    private void PaintStatus(Graphics g)
    {
        if (!StatusVisible) return;
        var rc = StatusRect();
        var r = new RectangleF(rc.X, rc.Y, rc.Width - 2, rc.Height - 2);
        Theme.FillRound(g, Color.FromArgb(184, 20, 20, 24), r, 14);
        Theme.DrawRound(g, Color.FromArgb(26, 255, 255, 255), r, 14);
        using (var dot = new SolidBrush(StatusOk ? Theme.Success : Theme.Danger))
            g.FillEllipse(dot, r.X + 12, r.Y + 11, 7, 7);
        TextRenderer.DrawText(g, _statusText, Theme.Small, new Rectangle((int)r.X + 26, (int)r.Y, (int)r.Width - 34, (int)r.Height), Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }

    private readonly object _gate = new();
    private Bitmap? _frame;
    private Rectangle _dest;

    private int _clickCount;
    private DateTime _lastClickTime;
    private Point _lastClickPos;
    private MouseButtons _lastClickButton;
    private Point _lastMove = new(-1, -1);

    /// <summary>x, y v rozsahu 0..1</summary>
    public event Action<float, float>? MouseMoved;
    /// <summary>tlačítko (0 L, 1 P, 2 S), stisk, počet kliknutí, x, y</summary>
    public event Action<byte, bool, byte, float, float>? MouseButtonChanged;
    /// <summary>dy, dx – delta ve stylu Windows (120 = jeden zub)</summary>
    public event Action<short, short>? Scrolled;
    /// <summary>Soubory/složky přetažené z Windows do okna + místo puštění (0..1, záporné = mimo obraz).</summary>
    public event Action<string[], float, float>? FilesDropped;
    /// <summary>Při stisknutém levém tlačítku opustila myš obraz – možná se na Macu táhne soubor ven.</summary>
    public event Action? DragLeftView;
    /// <summary>Dočasně nepřijímat soubory (během vlastního tažení ven).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SuppressDrop { get; set; }

    private bool _dragOutFired;
    private readonly RemoteCursor _cursor = new();

    /// <summary>Šířka zobrazeného displeje Macu v bodech (pt) – pro správnou velikost kurzoru.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double DisplayWidthPt { get; set; }

    /// <summary>Nový tvar kurzoru z Macu. Lze volat z libovolného vlákna; převezme bitmapu.</summary>
    public void SetRemoteCursor(Bitmap shape, float hotX, float hotY, float widthPt, float heightPt)
    {
        if (InvokeRequired)
        {
            try { BeginInvoke(() => SetRemoteCursor(shape, hotX, hotY, widthPt, heightPt)); }
            catch (InvalidOperationException) { shape.Dispose(); }
            return;
        }
        _cursor.SetShape(shape, hotX, hotY, widthPt, heightPt);
        ApplyCursor(force: true);
    }

    /// <summary>Měřítko: kolik pixelů okna připadá na 1 bod Macu.</summary>
    private float CursorScale()
    {
        Rectangle d;
        lock (_gate) d = _dest;
        if (d.Width <= 0 || DisplayWidthPt <= 0) return 1f;
        return (float)(d.Width / DisplayWidthPt);
    }

    private void ApplyCursor(bool force = false)
    {
        if (!_cursor.Update(CursorScale()) && !force) return;
        if (_savedCursor != null) _savedCursor = _cursor.Current; // právě nad dokem – použít až po opuštění
        else Cursor = _cursor.Current;
    }

    private bool _dragOver;
    private string? _toast;
    private Color _toastColor;
    private int _progress = -1;
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 3500 };

    /// <summary>Popis cíle zobrazený při přetahování.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string DropHint { get; set; } = "Pusťte nad oknem Finderu → uloží se do jeho složky  ·  nad aplikací → otevře se v ní  ·  jinde → na Plochu";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Placeholder { get; set; } = "Obraz není spuštěn";

    public RemoteView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Theme.ViewBg;
        ForeColor = Color.FromArgb(150, 150, 155);
        Cursor = _cursor.Current;
        AllowDrop = true;
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast = null; Invalidate(); };
    }

    // ---------------- oznámení a průběh ----------------

    /// <summary>Krátké oznámení dole uprostřed. sticky = nezmizí samo (např. během nahrávání).</summary>
    public void ShowToast(string text, Color? color = null, bool sticky = false, int progress = -1)
    {
        if (InvokeRequired) { BeginInvoke(() => ShowToast(text, color, sticky, progress)); return; }
        _toast = text;
        _toastColor = color ?? Theme.Accent;
        _progress = progress;
        _toastTimer.Stop();
        if (!sticky) _toastTimer.Start();
        Invalidate();
    }

    // ---------------- přetahování souborů ----------------

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (!SuppressDrop && e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
            _dragOver = true;
            Invalidate();
        }
        else e.Effect = DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (_dragOver) e.Effect = DragDropEffects.Copy;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _dragOver = false;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        _dragOver = false;
        Invalidate();
        if (SuppressDrop) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var p = PointToClient(new Point(e.X, e.Y));
            if (TryNormalize(p, out var x, out var y)) FilesDropped?.Invoke(files, x, y);
            else FilesDropped?.Invoke(files, -1, -1);
        }
    }

    private void PaintOverlays(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintDock(g);
        PaintPreview(g);
        PaintStatus(g);

        if (_dragOver)
        {
            // obraz zůstává vidět, aby šlo mířit na konkrétní okno
            var box = new RectangleF(3, 3, Width - 7, Height - 7);
            using (var path = Theme.RoundRect(box, 10))
            using (var pen = new Pen(Theme.Accent, 4) { DashStyle = DashStyle.Dash })
                g.DrawPath(pen, path);
            var size = TextRenderer.MeasureText(DropHint, Theme.Body);
            int w = Math.Min(Width - 40, size.Width + 70);
            var pill = new RectangleF((Width - w) / 2f, 18, w, 40);
            Theme.FillRound(g, Color.FromArgb(235, 30, 32, 38), pill, 12);
            Theme.DrawGlyph(g, Glyph.Upload, 13f, Theme.Accent, new Rectangle((int)pill.X + 10, (int)pill.Y, 30, 40));
            TextRenderer.DrawText(g, DropHint, Theme.Body, new Rectangle((int)pill.X + 42, (int)pill.Y, w - 50, 40), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if (_toast != null)
        {
            var size = TextRenderer.MeasureText(_toast, Theme.Body);
            int w = Math.Min(Width - 40, size.Width + 64);
            int h = _progress >= 0 ? 52 : 40;
            float bottom = Height - 24;
            var r = new RectangleF((Width - w) / 2f, bottom - h, w, h);
            Theme.FillRound(g, Color.FromArgb(235, 30, 32, 38), r, 12);
            Theme.DrawRound(g, Color.FromArgb(70, 255, 255, 255), r, 12);
            using (var dot = new SolidBrush(_toastColor))
                g.FillEllipse(dot, r.X + 16, r.Y + 15, 9, 9);
            TextRenderer.DrawText(g, _toast, Theme.Body, new Rectangle((int)r.X + 34, (int)r.Y + 8, w - 46, 24), Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (_progress >= 0)
            {
                var bar = new RectangleF(r.X + 16, r.Bottom - 14, w - 32, 4);
                Theme.FillRound(g, Color.FromArgb(70, 255, 255, 255), bar, 2);
                Theme.FillRound(g, _toastColor, new RectangleF(bar.X, bar.Y, bar.Width * _progress / 100f, 4), 2);
            }
        }
    }

    public bool HasFrame { get { lock (_gate) return _frame != null; } }

    private int _frameVersion;
    /// <summary>Zvyšuje se s každým novým snímkem (pro poloprůhledné panely).</summary>
    public int FrameVersion => Volatile.Read(ref _frameVersion);

    /// <summary>Malý (po zvětšení rozmazaný) výřez obrazu pod danou oblastí okna – pozadí poloprůhledných panelů.</summary>
    public Bitmap? BlurredSnapshot(Rectangle area, int factor = 12)
    {
        lock (_gate)
        {
            if (_frame == null || _dest.Width <= 0 || _dest.Height <= 0 || area.Width <= 0 || area.Height <= 0) return null;
            int w = Math.Max(2, area.Width / factor), h = Math.Max(2, area.Height / factor);
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Theme.Bg);
            var inter = Rectangle.Intersect(area, _dest);
            if (inter.Width > 0 && inter.Height > 0)
            {
                float sx = (float)_frame.Width / _dest.Width, sy = (float)_frame.Height / _dest.Height;
                float kx = (float)w / area.Width, ky = (float)h / area.Height;
                var src = new RectangleF((inter.X - _dest.X) * sx, (inter.Y - _dest.Y) * sy, inter.Width * sx, inter.Height * sy);
                var dst = new RectangleF((inter.X - area.X) * kx, (inter.Y - area.Y) * ky, inter.Width * kx, inter.Height * ky);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(_frame, dst, src, GraphicsUnit.Pixel);
            }
            return bmp;
        }
    }

    /// <summary>Vloží změněné obdélníky do obrazu (režim „jen změny“). Lze volat z libovolného vlákna;
    /// bitmapy záplat zůstávají volajícímu.</summary>
    public void ApplyPatch(int width, int height, bool reset, IReadOnlyList<(Rectangle Rect, Bitmap Image)> patches)
    {
        lock (_gate)
        {
            if (_frame == null || reset || _frame.Width != width || _frame.Height != height)
            {
                _frame?.Dispose();
                _frame = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var gc = Graphics.FromImage(_frame);
                gc.Clear(Color.Black);
            }
            using var g = Graphics.FromImage(_frame);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            foreach (var (rect, image) in patches) g.DrawImage(image, rect);
        }
        Interlocked.Increment(ref _frameVersion);
        if (IsHandleCreated && !IsDisposed)
        {
            try { BeginInvoke(new Action(Invalidate)); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Lze volat z libovolného vlákna. Převezme vlastnictví bitmapy.</summary>
    public void SetFrame(Bitmap? bmp)
    {
        Bitmap? old;
        lock (_gate)
        {
            old = _frame;
            _frame = bmp;
        }
        Interlocked.Increment(ref _frameVersion);
        old?.Dispose();
        if (IsHandleCreated && !IsDisposed)
        {
            try { BeginInvoke(new Action(Invalidate)); } catch (InvalidOperationException) { }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        lock (_gate)
        {
            if (_frame == null)
            {
                g.Clear(BackColor);
                Theme.DrawGlyph(g, Glyph.Desktop, 40f, Color.FromArgb(70, 70, 76), new Rectangle(0, Height / 2 - 90, Width, 70));
                TextRenderer.DrawText(g, Placeholder, Theme.Body, new Rectangle(40, Height / 2 - 10, Width - 80, 80), ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                PaintOverlays(g);
                return;
            }

            double scale = Math.Min((double)Width / _frame.Width, (double)Height / _frame.Height);
            int w = Math.Max(1, (int)(_frame.Width * scale));
            int h = Math.Max(1, (int)(_frame.Height * scale));
            var newDest = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);
            if (newDest.Width != _dest.Width) BeginInvoke(() => ApplyCursor());
            _dest = newDest;

            using (var bg = new SolidBrush(BackColor))
            {
                // jen okraje kolem obrazu
                using var region = new Region(ClientRectangle);
                region.Exclude(_dest);
                g.FillRegion(bg, region);
            }

            g.InterpolationMode = Math.Abs(scale - 1.0) < 0.01 ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(_frame, _dest);
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
        }
        PaintOverlays(g);
    }

    private bool TryNormalize(Point p, out float x, out float y)
    {
        Rectangle d;
        lock (_gate)
        {
            d = _dest;
            if (_frame == null || d.Width <= 0 || d.Height <= 0) { x = y = 0; return false; }
        }
        x = Math.Clamp((p.X - d.X) / (float)d.Width, 0f, 1f);
        y = Math.Clamp((p.Y - d.Y) / (float)d.Height, 0f, 1f);
        return true;
    }

    private static byte ButtonCode(MouseButtons b) => b switch
    {
        MouseButtons.Right => 1,
        MouseButtons.Middle => 2,
        _ => 0,
    };

    /// <summary>Vrací true, pokud právě tento počítač ovládá ten, který je v obrazu (nastavuje MainForm).</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<bool>? ControlledByTarget { get; set; }

    /// <summary>Pohyb myši vložený ovládáním na dálku z počítače, který je v obrazu → nepřeposílat zpět (smyčka).</summary>
    private bool IsLoopInput() =>
        global::RemoteScreen.Client.Host.WinNative.CurrentMessageIsRemote() && (ControlledByTarget?.Invoke() ?? false);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsLoopInput()) return;
        if (_statusDown)
        {
            if (Math.Abs(e.X - _statusGrab.X) > 3 || Math.Abs(e.Y - _statusGrab.Y) > 3) _statusMoved = true;
            if (_statusMoved && Width > 0 && Height > 0)
            {
                var before = StatusRect();
                var r0 = before;
                int nx = e.X - _statusGrabOffset.X, ny = e.Y - _statusGrabOffset.Y;
                _statusPos = new PointF(nx / (float)Width, ny / (float)Height);
                Invalidate(Rectangle.Inflate(Rectangle.Union(r0, StatusRect()), 3, 3));
            }
            return;
        }
        if (StatusVisible && StatusRect().Contains(e.Location) && e.Button == MouseButtons.None)
        {
            if (_savedCursor == null) { _savedCursor = Cursor; }
            Cursor = Cursors.SizeAll;
            return;
        }
        if (_handleDown)
        {
            // tažení šipky nahoru/dolů
            if (Math.Abs(e.Y - _handleDownY) > 4) _handleMoved = true;
            if (_handleMoved && Height > 0)
            {
                var before = DockInvalidRect();
                _dockPos = Math.Clamp(e.Y / (float)Height, 0f, 1f);
                LayoutDock();
                Invalidate(Rectangle.Union(before, DockInvalidRect()));
                Invalidate(StatusRect());
            }
            return;
        }
        int hit = _downOnDock || e.Button == MouseButtons.None ? DockHit(e.Location) : HitOutside;
        int hover = hit == HitOutside ? -1 : hit;
        if (hover != _dockHover)
        {
            Invalidate(DockInvalidRect()); // včetně starého náhledu
            _dockHover = hover;
            if (hover >= 0 && hover < DockItems.Count) DockItems[hover].Hover?.Invoke();
            _previewRect = PreviewRect();
            Invalidate(DockInvalidRect());
            _dockTip.SetToolTip(this, hover >= 0 && _itemH < 46 && hover < DockItems.Count ? DockItems[hover].Label : null);
        }
        if (IsDockHit(hit) || _downOnDock)
        {
            // nad dokem: běžná šipka, nic neposílat
            if (_savedCursor == null) _savedCursor = Cursor;
            Cursor = Cursors.Default;
            return;
        }
        if (_savedCursor != null) { Cursor = _savedCursor; _savedCursor = null; }
        if (e.Location == _lastMove) return;
        _lastMove = e.Location;
        if (e.Button == MouseButtons.Left && !_dragOutFired && !ClientRectangle.Contains(e.Location))
        {
            _dragOutFired = true;
            DragLeftView?.Invoke();
        }
        if (TryNormalize(e.Location, out var x, out var y)) MouseMoved?.Invoke(x, y);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsLoopInput()) return;
        Focus();
        if (e.Button == MouseButtons.Left && StatusVisible && StatusRect().Contains(e.Location))
        {
            _statusDown = true;
            _statusMoved = false;
            _statusGrab = e.Location;
            var sr = StatusRect();
            _statusGrabOffset = new Point(e.X - sr.X, e.Y - sr.Y);
            return;
        }
        int hit = DockHit(e.Location);
        if (hit == HitHandle && e.Button == MouseButtons.Left)
        {
            _handleDown = true;
            _handleMoved = false;
            _handleDownY = e.Y;
            Invalidate(DockInvalidRect());
            return;
        }
        if (IsDockHit(hit))
        {
            _downOnDock = true;
            _dockPressed = e.Button == MouseButtons.Left ? hit : -1;
            Invalidate(DockInvalidRect());
            return;
        }
        _dragOutFired = false;
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right or MouseButtons.Middle)) return;

        var now = DateTime.UtcNow;
        var ds = SystemInformation.DoubleClickSize;
        bool sameSpot = Math.Abs(e.X - _lastClickPos.X) <= ds.Width && Math.Abs(e.Y - _lastClickPos.Y) <= ds.Height;
        if (e.Button == _lastClickButton && sameSpot &&
            (now - _lastClickTime).TotalMilliseconds <= SystemInformation.DoubleClickTime)
            _clickCount = Math.Min(_clickCount + 1, 3);
        else
            _clickCount = 1;
        _lastClickTime = now;
        _lastClickPos = e.Location;
        _lastClickButton = e.Button;

        if (TryNormalize(e.Location, out var x, out var y))
            MouseButtonChanged?.Invoke(ButtonCode(e.Button), true, (byte)_clickCount, x, y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (IsLoopInput()) return;
        if (_statusDown)
        {
            _statusDown = false;
            if (!_statusMoved)
            {
                // dvojklik = vrátit do výchozího rohu
                if ((DateTime.UtcNow - _statusLastClick).TotalMilliseconds <= SystemInformation.DoubleClickTime && _statusPos != null)
                {
                    _statusPos = null;
                    Invalidate();
                    StatusMoved?.Invoke();
                }
                _statusLastClick = DateTime.UtcNow;
            }
            else StatusMoved?.Invoke();
            return;
        }
        if (_handleDown)
        {
            _handleDown = false;
            if (!_handleMoved) _dockOpen = !_dockOpen; // klik = vysunout/zasunout
            LayoutDock();
            Invalidate();
            DockStateChanged?.Invoke();
            return;
        }
        if (_downOnDock)
        {
            _downOnDock = false;
            int pressed = _dockPressed;
            _dockPressed = -1;
            Invalidate(DockInvalidRect());
            if (pressed >= 0 && DockHit(e.Location) == pressed && pressed < DockItems.Count)
                BeginInvoke(() => { DockItems[pressed].Click?.Invoke(); Invalidate(); });
            return;
        }
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right or MouseButtons.Middle)) return;
        if (TryNormalize(e.Location, out var x, out var y))
            MouseButtonChanged?.Invoke(ButtonCode(e.Button), false, (byte)Math.Max(1, _clickCount), x, y);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (IsLoopInput()) return;
        if (IsDockHit(DockHit(e.Location))) return;
        Scrolled?.Invoke((short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue), 0);
    }

    private const int WM_MOUSEHWHEEL = 0x020E;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEHWHEEL)
        {
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            Scrolled?.Invoke(0, (short)-delta);
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dockHover != -1) { Invalidate(DockInvalidRect()); _dockHover = -1; _previewRect = Rectangle.Empty; }
        if (_savedCursor != null) { Cursor = _savedCursor; _savedCursor = null; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toastTimer.Dispose();
            _cursor.Dispose();
            lock (_gate)
            {
                _frame?.Dispose();
                _frame = null;
            }
        }
        base.Dispose(disposing);
    }
}
