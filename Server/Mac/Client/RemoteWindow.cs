using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteScreen.Shared;
using Message = RemoteScreen.Shared.Message;

namespace RemoteScreen.Server;

/// <summary>Okno se vzdálenou plochou jiného počítače (Mac nebo Windows) – ovládání z tohoto Macu.</summary>
internal sealed partial class RemoteWindow : IWindowHandler
{
    /// <summary>Otevřená ovládací okna (pro menu v liště a ukončení aplikace).</summary>
    public static readonly List<RemoteWindow> Open = new();
    private static readonly Dictionary<IntPtr, RemoteWindow> ByView = new();

    private static readonly int[] FpsOptions = { 30, 60, 90, 120, 144 };

    private readonly RemoteClient _client;
    private readonly FileTransfers _files;
    private readonly MacClientSettings _settings;
    private readonly bool _targetWindows;
    private readonly DisplayInfo[] _displays;
    public string Title { get; }

    private IntPtr _window, _content, _view, _placeholder, _dock, _swapButton, _fpsButton;
    private IntPtr _pill, _pillDot, _pillLabel;
    private bool _sendKeys = true;
    private int _lastFps;
    private long _lastBytes;

    /// <summary>Položka doku: tlačítko s ikonou a popiskem (jako na Windows).</summary>
    private sealed record DockEntry(IntPtr Button, Func<bool>? Active, bool Danger);
    private readonly List<DockEntry> _dockEntries = new();
    private IntPtr _toast, _toastLabel, _timer;
    private readonly List<IntPtr> _controls = new();
    private bool _closed, _closing, _streaming;
    private int _displayIndex;
    private DateTime _toastUntil = DateTime.MaxValue;

    // obraz
    private byte[]? _pendingJpeg;
    private int _decoding, _presentQueued;
    private IntPtr _readyImage, _image;
    private double _imgW, _imgH;
    private int _frames;
    private long _bytes;
    private double _pingMs = -1;

    // kurzor
    private IntPtr _cursor;
    private (float HotX, float HotY, float W, float H, byte[] Png)? _cursorShape;

    // vstup
    private readonly HashSet<ushort> _keysDown = new();
    private readonly HashSet<ushort> _autoReleased = new();
    private bool _leftDown, _dragOutTried, _suppressLeftUp;
    private float _lastX = 0.5f, _lastY = 0.5f;
    private double _scrollAccY, _scrollAccX;

    // čekání na odpovědi serveru
    private TaskCompletionSource<DropTarget>? _dropTargetWaiter;
    private TaskCompletionSource<DragFiles>? _dragFilesWaiter;

    private RemoteWindow(RemoteClient client, string name, MacClientSettings settings)
    {
        _client = client;
        _files = new FileTransfers(client);
        _settings = settings;
        _targetWindows = client.Info.Platform == "windows";
        _displays = client.Info.Displays ?? Array.Empty<DisplayInfo>();
        _displayIndex = Math.Max(0, Array.FindIndex(_displays, d => d.IsMain));
        Title = name;
        UI.SetAccent(settings.Accent);
    }

    /// <summary>Otevře okno pro připojeného klienta (volat na hlavním vlákně).</summary>
    public static void Start(RemoteClient client, string name, MacClientSettings settings)
    {
        var w = new RemoteWindow(client, name, settings);
        Open.Add(w);
        w.Build();
        client.MessageReceived += w.OnMessage;
        client.Disconnected += w.OnDisconnected;
        client.Start();
        w.StartStream();
        if (settings.ClipboardSync) w.StartClipSync();
        CleanupDragTemp();
    }

    // ---------------- okno ----------------

    private (double W, double H) RemoteSize()
    {
        var d = _displays.ElementAtOrDefault(_displayIndex);
        return d != null && d.Width > 0 && d.Height > 0 ? (d.Width, d.Height) : (1600, 1000);
    }

    private void Build()
    {
        var (rw, rh) = RemoteSize();
        IntPtr screen = ObjC.Get(ObjC.Cls("NSScreen"), "mainScreen");
        var visible = ObjC.Rect(screen, "visibleFrame");
        double maxW = visible.W * 0.88, maxH = visible.H * 0.88 - 28;
        double scale = Math.Min(1, Math.Min(maxW / rw, maxH / rh));
        double w = Math.Round(rw * scale), h = Math.Round(rh * scale);

        _window = UI.CreateWindow(new CGRect(0, 0, w, h),
            UI.StyleTitled | UI.StyleClosable | UI.StyleMiniaturizable | UI.StyleResizable,
            $"{Title} — RemoteScreen", this);
        ObjC.Set(_window, "setBackgroundColor:", ObjC.Color(0, 0, 0));
        ObjC.SendSize(_window, ObjC.Sel("setContentAspectRatio:"), new CGSize(rw, rh));
        ObjC.SendSize(_window, ObjC.Sel("setContentMinSize:"), new CGSize(480, 360));
        ObjC.SendUInt(_window, ObjC.Sel("setCollectionBehavior:"), 1 << 7); // celá obrazovka

        _content = ObjC.NewWithFrame("NSView", new CGRect(0, 0, w, h));
        UI.Rounded(_content, 0, ObjC.Color(0, 0, 0));
        ObjC.Set(_window, "setContentView:", _content);

        // plocha
        _view = ObjC.NewWithFrame(RemoteViewClass(), new CGRect(0, 0, w, h));
        ByView[_view] = this;
        UI.Autoresize(_view, 2 | 16);
        ObjC.SetBool(_view, "setWantsLayer:", true);
        ObjC.SetInt(_view, "setLayerContentsRedrawPolicy:", 1); // jen při setNeedsDisplay
        IntPtr tracking = ObjC.SendInitTracking(ObjC.Send(ObjC.Cls("NSTrackingArea"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithRect:options:owner:userInfo:"), new CGRect(0, 0, 0, 0),
            0x02 | 0x04 | 0x40 | 0x200 /* MouseMoved | CursorUpdate | ActiveInActiveApp | InVisibleRect */, _view, IntPtr.Zero);
        ObjC.Send(_view, ObjC.Sel("addTrackingArea:"), tracking);
        IntPtr types = ObjC.Send(ObjC.Cls("NSArray"), ObjC.Sel("arrayWithObject:"), ObjC.Str("public.file-url"));
        ObjC.Send(_view, ObjC.Sel("registerForDraggedTypes:"), types);
        UI.Add(_content, _view);

        _placeholder = UI.Label("Čekám na obraz…", 15, UI.WeightMedium, UI.TextDim);
        ObjC.SetInt(_placeholder, "setAlignment:", 2); // na střed
        UI.Frame(_placeholder, 20, h / 2 - 12, w - 40, 24);
        UI.Autoresize(_placeholder, 2 | 8 | 32);
        UI.Add(_content, _placeholder);

        BuildDock(w);
        BuildToast(w, h);

        UI.ShowWindow(_window);
        ObjC.Send(_window, ObjC.Sel("center"));
        ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _view);
        _timer = UI.Timer(1.0, Tick);
        MenuBarApp.RefreshIcon();

        Toast(_targetWindows && _settings.SwapCmdCtrl
            ? $"Připojeno k {Title}. Cmd funguje jako Ctrl (Cmd+C, Cmd+V…). Menu je vpravo u šipky."
            : $"Připojeno k {Title}. Menu s nástroji je vpravo u šipky.");
    }

    private void BuildDock(double windowWidth)
    {
        string target = _targetWindows ? "PC" : "Mac";
        var items = new List<(string Symbol, string Label, string Tip, Action Click, Func<bool>? Active, bool Danger, bool Sep)>
        {
            ("slider.horizontal.3", "Ovládání", "Zvuk, hudba, schránka, aplikace, systém", () => TogglePanel(PanelKind.Control), () => _open == PanelKind.Control, false, false),
            ("gearshape", "Obraz", "Monitor, kvalita, snímková frekvence, klávesy", () => TogglePanel(PanelKind.Settings), () => _open == PanelKind.Settings, false, true),
            ("lock", "Zámek", $"Zamknout obrazovku ({target})", () => { RunAction("lock"); Toast($"Zamykám {target}…"); }, null, false, false),
            ("arrow.up.left.and.arrow.down.right", "Celá obrazovka", "Celá obrazovka", () => ObjC.Send(_window, ObjC.Sel("toggleFullScreen:"), IntPtr.Zero), IsFullScreen, false, false),
            ("xmark", "Odpojit", "Odpojit", Disconnect, null, true, true),
        };
        // víc obrazovek na druhém počítači → rychlé přepínání přímo v menu (hned pod „Obraz“)
        // víc obrazovek → tlačítko pro každou (hned pod „Obraz“), po najetí myší malý náhled
        if (_displays.Length > 1)
        {
            for (int i = 0; i < _displays.Length; i++)
            {
                int idx = i;
                var d = _displays[i];
                items.Insert(2 + i, ("display", $"Monitor {i + 1}", $"Monitor {i + 1}: {d.Width:0}×{d.Height:0}{(d.IsMain ? " (hlavní)" : "")}",
                    () => { if (_displayIndex != idx) SelectDisplay(idx); }, () => _displayIndex == idx, false, false));
                _monitorDock.Add((2 + i, idx));
            }
        }

        // svislé menu vpravo (ikona nad popiskem), vysouvá se za šipkou
        int seps = items.Skip(1).Count(i => i.Sep);
        _dockH = 12 + items.Count * DockItemH + seps * 9;
        _dock = UI.Glass(new CGRect(0, 0, DockW, _dockH), 13 /* HUD */, behindWindow: false, cornerRadius: 20);
        UI.Rounded(_dock, 20, null, ObjC.Color(1, 1, 1, 0.10));
        _dockColumn = ObjC.NewWithFrame(ConnectWindow.FlippedViewClass(), new CGRect(0, 0, DockW, _dockH));
        UI.Add(_dock, _dockColumn);
        IntPtr column = _dockColumn;
        double y = 6;
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Sep && i > 0)
            {
                IntPtr line = ObjC.NewWithFrame("NSView", new CGRect(20, y + 4, DockW - 40, 1));
                UI.Rounded(line, 0, ObjC.Color(1, 1, 1, 0.12));
                UI.Add(column, line);
                _dockLines.Add(line);
                y += 9;
            }
            IntPtr b = Btn(it.Symbol, it.Tip, it.Click);
            ObjC.Set(b, "setTitle:", ObjC.Str(it.Label));
            ObjC.Set(b, "setFont:", ObjC.Font(10.5, UI.WeightMedium));
            ObjC.SetInt(b, "setImagePosition:", 5); // ikona nad textem
            UI.Rounded(b, 12);
            UI.Frame(b, 6, y, DockW - 12, DockItemH);
            UI.Add(column, b);
            _dockEntries.Add(new DockEntry(b, it.Active, it.Danger));
            _dockSepBefore.Add(it.Sep && i > 0);
            _dockLabels.Add(it.Label);
            y += DockItemH;
        }
        UI.Add(_content, _dock);

        // šipka (úchyt): kliknutí = vysunout/zasunout, tažení = posunout nahoru/dolů
        _handle = UI.Glass(new CGRect(0, 0, HandleW, HandleH), 13, behindWindow: false, cornerRadius: 9);
        UI.Rounded(_handle, 9, null, ObjC.Color(1, 1, 1, 0.14));
        _handleIcon = ObjC.NewWithFrame("NSImageView", new CGRect(2, (HandleH - 16) / 2, HandleW - 4, 16));
        if (ObjC.RespondsTo(_handleIcon, "setContentTintColor:")) ObjC.Set(_handleIcon, "setContentTintColor:", UI.TextDim);
        UI.Add(_handle, _handleIcon);
        IntPtr grip = ObjC.NewWithFrame(HandleViewClass(), new CGRect(0, 0, HandleW, HandleH));
        ByHandle[grip] = this;
        _handleGrip = grip;
        ObjC.Set(grip, "setToolTip:", ObjC.Str("Menu – kliknutím vysunete/zasunete, tažením posunete"));
        UI.Add(_handle, grip);
        UI.Add(_content, _handle);
        _dockOpen = _settings.DockOpen;
        _dockPos = Math.Clamp(_settings.DockPosition, 0, 1);
        RefreshDock();

        // stavová pilulka vpravo nahoře
        var bounds = ObjC.Rect(_content, "bounds");
        _pill = UI.Glass(new CGRect(bounds.W - 300 - 14, bounds.H - 30 - 12, 300, 30), 13, behindWindow: false, cornerRadius: 15);
        UI.Rounded(_pill, 15, null, ObjC.Color(1, 1, 1, 0.10));
        UI.Autoresize(_pill, 1 | 8);
        _pillDot = ObjC.NewWithFrame("NSView", new CGRect(12, 11.5, 7, 7));
        UI.Rounded(_pillDot, 3.5, UI.Success);
        UI.Add(_pill, _pillDot);
        _pillLabel = UI.Label("", 11.5, UI.WeightRegular, UI.TextDim);
        ObjC.SetInt(_pillLabel, "setLineBreakMode:", 4);
        UI.Frame(_pillLabel, 26, 7, 264, 16);
        UI.Autoresize(_pillLabel, 2);
        UI.Add(_pill, _pillLabel);
        _pillGrip = ObjC.NewWithFrame(HandleViewClass(), new CGRect(0, 0, 300, 30));
        UI.Autoresize(_pillGrip, 2 | 16);
        ObjC.Set(_pillGrip, "setToolTip:", ObjC.Str("Tažením přesunete, dvojklikem vrátíte do rohu"));
        ByHandle[_pillGrip] = this;
        UI.Add(_pill, _pillGrip);
        UI.Add(_content, _pill);
        if (_settings.PillX >= 0 && _settings.PillY >= 0) _pillPos = (_settings.PillX, _settings.PillY);
        LayoutDock();
    }

    /// <summary>Položky menu „Monitor N“: (index položky v menu, index obrazovky v _displays).</summary>
    private readonly List<(int Entry, int Display)> _monitorDock = new();
    private readonly Dictionary<int, (IntPtr Image, double W, double H)> _thumbs = new(); // index displeje → CGImage
    private readonly Dictionary<int, DateTime> _thumbAsked = new();
    private IntPtr _preview, _previewImage, _previewLabel;
    private int _previewFor = -1;

    /// <summary>Po přepnutí obrazovky: zvýraznění v menu a oznámení.</summary>
    private void UpdateMonitorItem()
    {
        if (_monitorDock.Count == 0) return;
        RefreshDock();
        var d = _displays[_displayIndex];
        Toast($"Monitor {_displayIndex + 1} z {_displays.Length} · {d.Width:0}×{d.Height:0}{(d.IsMain ? " (hlavní)" : "")}");
    }

    /// <summary>Myš nad menu (souřadnice obsahu okna): nad „Monitor N“ ukázat náhled, jinde ho schovat.</summary>
    private void UpdateMonitorHover(CGPoint? p)
    {
        int hovered = -1, entry = -1;
        if (p is CGPoint pt && _dockOpen)
        {
            foreach (var (e, disp) in _monitorDock)
            {
                if (e >= _dockEntries.Count) continue;
                IntPtr b = _dockEntries[e].Button;
                var bp = ObjC.SendPointObjRetPoint(b, ObjC.Sel("convertPoint:fromView:"), pt, _content);
                var bb = ObjC.Rect(b, "bounds");
                if (bp.X >= 0 && bp.Y >= 0 && bp.X < bb.W && bp.Y < bb.H) { hovered = disp; entry = e; break; }
            }
        }
        if (hovered < 0) { HidePreview(); return; }
        RequestThumb(hovered);
        ShowPreview(hovered, entry);
    }

    private void RequestThumb(int i)
    {
        int index = _displays[i].Index;
        if (_thumbAsked.TryGetValue(index, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(1.5)) return;
        _thumbAsked[index] = DateTime.UtcNow;
        _client.Post(MsgType.ThumbGet, new[] { (byte)index });
    }

    /// <summary>Náhled dorazil (vlákno čtení) → dekódovat, uložit, případně ukázat.</summary>
    private void OnThumb(byte[] payload)
    {
        if (payload.Length < 2) return;
        IntPtr img = ClientNative.DecodeJpeg(payload[1..]);
        if (img == IntPtr.Zero) return;
        double w = Native.CGImageGetWidth(img), h = Native.CGImageGetHeight(img);
        int index = payload[0];
        MenuBarApp.RunOnMain(() =>
        {
            if (_closed) { Native.CGImageRelease(img); return; }
            if (_thumbs.TryGetValue(index, out var old)) Native.CGImageRelease(old.Image);
            _thumbs[index] = (img, w, h);
            if (_previewFor >= 0 && _displays[_previewFor].Index == index)
            {
                int entry = _monitorDock.FirstOrDefault(m => m.Display == _previewFor).Entry;
                _previewFor = -1; // vynutit překreslení
                ShowPreview(_displays.ToList().FindIndex(d => d.Index == index), entry);
            }
        });
    }

    private void ShowPreview(int disp, int entry)
    {
        if (!_thumbs.TryGetValue(_displays[disp].Index, out var t)) { HidePreview(); return; } // náhled ještě nedorazil
        const double W = 240, Pad = 6, CapH = 22;
        double ih = Math.Clamp(W * t.H / Math.Max(1, t.W), 60, 180);
        double pw = W + 2 * Pad, ph = ih + 2 * Pad + CapH;
        if (_preview == IntPtr.Zero)
        {
            _preview = UI.Glass(new CGRect(0, 0, pw, ph), 13, behindWindow: false, cornerRadius: 14, passThrough: true);
            UI.Rounded(_preview, 14, null, ObjC.Color(1, 1, 1, 0.10));
            _previewImage = ObjC.NewWithFrame("NSImageView", new CGRect(Pad, CapH + Pad, W, ih));
            ObjC.SetInt(_previewImage, "setImageScaling:", 3); // NSImageScaleProportionallyUpOrDown
            UI.Add(_preview, _previewImage);
            _previewLabel = UI.Label("", 11, UI.WeightRegular, UI.TextDim);
            ObjC.SetInt(_previewLabel, "setAlignment:", 1); // na střed
            UI.Add(_preview, _previewLabel);
            UI.Add(_content, _preview);
        }
        if (_previewFor != disp)
        {
            _previewFor = disp;
            IntPtr nsImage = ObjC.SendObjSize(ObjC.Send(ObjC.Cls("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithCGImage:size:"), t.Image, new CGSize(t.W, t.H));
            ObjC.Set(_previewImage, "setImage:", nsImage);
            ObjC.Send(nsImage, ObjC.Sel("release"));
            var d = _displays[disp];
            UI.SetText(_previewLabel, $"Monitor {disp + 1} · {d.Width:0}×{d.Height:0}{(d.IsMain ? " · hlavní" : "")}");
        }
        UI.Frame(_previewImage, Pad, CapH + Pad, W, ih);
        UI.Frame(_previewLabel, Pad, 4, W, 16);

        // vlevo od položky, svisle na její úrovni, nikdy mimo obraz
        IntPtr b = _dockEntries[entry].Button;
        var bb = ObjC.Rect(b, "bounds");
        var c1 = ObjC.SendPointObjRetPoint(b, ObjC.Sel("convertPoint:toView:"), new CGPoint(0, 0), _content);
        var c2 = ObjC.SendPointObjRetPoint(b, ObjC.Sel("convertPoint:toView:"), new CGPoint(bb.W, bb.H), _content);
        double midY = (c1.Y + c2.Y) / 2;
        var content = ObjC.Rect(_content, "bounds");
        var dock = ObjC.Rect(_dock, "frame");
        var handle = ObjC.Rect(_handle, "frame");
        double x = Math.Max(10, Math.Min(dock.X, handle.X) - pw - 8); // vlevo od menu i od šipky
        double y = Math.Clamp(midY - ph / 2, 10, Math.Max(10, content.H - ph - 10));
        UI.Frame(_preview, x, y, pw, ph);
        ObjC.SetBool(_preview, "setHidden:", false);
    }

    private void HidePreview()
    {
        if (_preview != IntPtr.Zero) ObjC.SetBool(_preview, "setHidden:", true);
        _previewFor = -1;
    }

    private bool IsFullScreen() => (ObjC.SendRetUInt(_window, ObjC.Sel("styleMask")) & (1 << 14)) != 0;

    /// <summary>Zvýraznění aktivních položek doku (oranžově), Odpojit červeně.</summary>
    private void RefreshDock()
    {
        if (_closed) return;
        foreach (var e in _dockEntries)
        {
            bool active = e.Active?.Invoke() == true;
            IntPtr layer = ObjC.Get(e.Button, "layer");
            if (layer != IntPtr.Zero)
                ObjC.Set(layer, "setBackgroundColor:", ObjC.Get(active ? UI.AccentA(0.16) : UI.Clear, "CGColor"));
            if (ObjC.RespondsTo(e.Button, "setContentTintColor:"))
                ObjC.Set(e.Button, "setContentTintColor:", e.Danger ? ObjC.Color(1.0, 0.54, 0.50) : active ? UI.AccentText : UI.Text);
        }
    }

    private void UpdatePill()
    {
        if (_closed || _pill == IntPtr.Zero) return;
        // na celé obrazovce se zavřeným menu schovat i stav
        // na celé obrazovce se zavřeným menu schovat (průběh přenosu je v oznámení dole)
        bool hide = !_dockOpen && IsFullScreen();
        ObjC.SetBool(_pill, "setHidden:", hide);
        if (hide) return;
        string ping = _pingMs < 0 ? "–" : $"{_pingMs:0} ms";
        string text = _xfer != null ? $"{Title} · {_xfer} · {FormatRate(_lastXferBytes)}"
            : !_streaming ? $"{Title} · obraz zastaven · {ping}"
            : _lastFps == 0 ? $"{Title} · obraz beze změn · {ping}"
            : $"{Title} · {_lastFps} fps · {ping} · {_lastBytes / 1024} kB/s";
        if (_sendKeys && ObjC.SendRetBool0(_window, ObjC.Sel("isKeyWindow"))) text += "  ·  ⌨";
        UI.SetText(_pillLabel, text);
        var b = ObjC.Rect(_content, "bounds");
        double w = Math.Min(b.W - 28, text.Length * 6.6 + 44);
        if (_pillPos is { } pp)
        {
            var (fx, fy) = pp;
            // přesunutá uživatelem: levý horní roh jako zlomek okna (y shora)
            double px = Math.Clamp(fx * b.W, 4, Math.Max(4, b.W - w - 4));
            double pyTop = Math.Clamp(fy * b.H, 4, Math.Max(4, b.H - 34));
            UI.Frame(_pill, px, b.H - pyTop - 30, w, 30);
            return;
        }
        double right = b.W - 14, top = b.H - 12 - 30;
        // s vysunutým menu pilulku posunout vlevo, pokud by se překrývaly
        if (_dockOpen && _dock != IntPtr.Zero)
        {
            var df = ObjC.Rect(_dock, "frame");
            if (df.Y + df.H > top - 6) right = df.X - 10;
        }
        if (_handle != IntPtr.Zero)
        {
            var hf = ObjC.Rect(_handle, "frame");
            if (hf.Y + hf.H > top - 6) right = Math.Min(right, hf.X - 8);
        }
        UI.Frame(_pill, right - w, top, w, 30);
    }

    private const double DockW = 100, DockItemH = 54, HandleW = 20, HandleH = 60, HandleGap = 6;
    private double _dockH, _dockItemH = DockItemH;
    private IntPtr _dockColumn;
    private readonly List<IntPtr> _dockLines = new();
    private readonly List<bool> _dockSepBefore = new();
    private readonly List<string> _dockLabels = new();

    /// <summary>Menu nikdy nesmí přesáhnout obraz: v nízkém okně zmenšit položky (pod 46 pt jen ikony).</summary>
    private void FitDockItems(double height)
    {
        int n = _dockEntries.Count, seps = _dockSepBefore.Count(x => x);
        if (n == 0) return;
        double itemH = Math.Clamp(Math.Floor((height - 20 - 12 - seps * 9) / n), 28, DockItemH);
        if (Math.Abs(itemH - _dockItemH) < 0.5 && _dockH > 0) return;
        _dockItemH = itemH;
        bool compact = itemH < 46;
        double y = 6;
        int line = 0;
        for (int i = 0; i < n; i++)
        {
            if (_dockSepBefore[i])
            {
                if (line < _dockLines.Count) UI.Frame(_dockLines[line++], 20, y + 4, DockW - 40, 1);
                y += 9;
            }
            IntPtr b = _dockEntries[i].Button;
            UI.Frame(b, 6, y, DockW - 12, itemH);
            ObjC.Set(b, "setTitle:", ObjC.Str(compact ? "" : _dockLabels[i]));
            ObjC.SetInt(b, "setImagePosition:", compact ? 1 : 5); // jen ikona / ikona nad textem
            y += itemH;
        }
        _dockH = y + 6;
        UI.Frame(_dockColumn, 0, 0, DockW, _dockH);
    }
    private IntPtr _handle, _handleIcon, _handleGrip, _pillGrip;
    private (double X, double Y)? _pillPos;
    private CGPoint _pillGrab;
    private bool _dockOpen = true;
    private double _dockPos = 0.5; // 0 = nahoře, 1 = dole

    /// <summary>Poloha menu a šipky vpravo podle uložené svislé polohy.</summary>
    private void LayoutDock()
    {
        if (_closed || _handle == IntPtr.Zero) return;
        var b = ObjC.Rect(_content, "bounds");
        FitDockItems(b.H);
        double cyTop = Math.Clamp(_dockPos * b.H, HandleH / 2 + 8, Math.Max(HandleH / 2 + 8, b.H - HandleH / 2 - 8));
        double cy = b.H - cyTop; // souřadnice zdola
        if (_dockOpen)
        {
            double y = Math.Clamp(cy - _dockH / 2, 10, Math.Max(10, b.H - _dockH - 10));
            double x = b.W - DockW - 10;
            UI.Frame(_dock, x, y, DockW, _dockH);
            ObjC.SetBool(_dock, "setHidden:", false);
            UI.Frame(_handle, x - HandleW - HandleGap, cy - HandleH / 2, HandleW, HandleH); // mezera mezi šipkou a menu
        }
        else
        {
            ObjC.SetBool(_dock, "setHidden:", true);
            UI.Frame(_handle, b.W - HandleW, cy - HandleH / 2, HandleW, HandleH);
        }
        IntPtr chevron = UI.Symbol(_dockOpen ? "chevron.right" : "chevron.left", 12);
        if (chevron != IntPtr.Zero) ObjC.Set(_handleIcon, "setImage:", chevron);
        UpdatePill();
    }

    private void ToggleDock()
    {
        _dockOpen = !_dockOpen;
        HidePreview();
        SaveDock();
        LayoutDock();
    }

    private void SaveDock()
    {
        _settings.DockOpen = _dockOpen;
        _settings.DockPosition = _dockPos;
        _settings.Save();
    }

    // ---------------- šipka: vlastní NSView kvůli tažení ----------------

    private static readonly Dictionary<IntPtr, RemoteWindow> ByHandle = new();
    private static IntPtr _handleClass;
    private double _gripDownY, _gripDownX;
    private bool _gripMoved;

    private static unsafe IntPtr HandleViewClass()
    {
        if (_handleClass != IntPtr.Zero) return _handleClass;
        IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSView"), "RSHandleView", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("mouseDown:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnGripDown, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("mouseDragged:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnGripDragged, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("mouseUp:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnGripUp, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("acceptsFirstMouse:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&GripFirstMouse, "c@:@");
        ObjC.objc_registerClassPair(cls);
        return _handleClass = cls;
    }

    [UnmanagedCallersOnly] private static byte GripFirstMouse(IntPtr self, IntPtr sel, IntPtr ev) => 1;

    private CGPoint ContentPoint(IntPtr ev) =>
        ObjC.SendPointObjRetPoint(_content, ObjC.Sel("convertPoint:fromView:"), ObjC.SendRetPoint(ev, ObjC.Sel("locationInWindow")), IntPtr.Zero);

    [UnmanagedCallersOnly]
    private static void OnGripDown(IntPtr self, IntPtr sel, IntPtr ev)
    {
        try
        {
            if (!ByHandle.TryGetValue(self, out var w) || w._closed) return;
            var p = w.ContentPoint(ev);
            w._gripDownY = p.Y;
            w._gripMoved = false;
            if (self == w._pillGrip)
            {
                var f = ObjC.Rect(w._pill, "frame");
                w._pillGrab = new CGPoint(p.X - f.X, p.Y - f.Y);
                w._gripDownX = p.X;
            }
        }
        catch (Exception ex) { Log.Error("Šipka menu: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    private static void OnGripDragged(IntPtr self, IntPtr sel, IntPtr ev)
    {
        try
        {
            if (!ByHandle.TryGetValue(self, out var w) || w._closed) return;
            var p = w.ContentPoint(ev);
            if (self == w._pillGrip)
            {
                if (Math.Abs(p.Y - w._gripDownY) > 3 || Math.Abs(p.X - w._gripDownX) > 3) w._gripMoved = true;
                if (!w._gripMoved) return;
                var b = ObjC.Rect(w._content, "bounds");
                if (b.W <= 0 || b.H <= 0) return;
                double x = p.X - w._pillGrab.X, yBottom = p.Y - w._pillGrab.Y;
                w._pillPos = (x / b.W, (b.H - yBottom - 30) / b.H);
                w.UpdatePill();
                return;
            }
            if (Math.Abs(p.Y - w._gripDownY) > 4) w._gripMoved = true;
            if (!w._gripMoved) return;
            double h = ObjC.Rect(w._content, "bounds").H;
            if (h <= 0) return;
            w._dockPos = Math.Clamp(1 - p.Y / h, 0, 1);
            w.LayoutDock();
        }
        catch (Exception ex) { Log.Error("Šipka menu: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    private static void OnGripUp(IntPtr self, IntPtr sel, IntPtr ev)
    {
        try
        {
            if (!ByHandle.TryGetValue(self, out var w) || w._closed) return;
            if (self == w._pillGrip)
            {
                // dvojklik = zpět do výchozího rohu
                if (!w._gripMoved && ObjC.SendRetInt(ev, ObjC.Sel("clickCount")) >= 2) w._pillPos = null;
                w._settings.PillX = w._pillPos?.X ?? -1;
                w._settings.PillY = w._pillPos?.Y ?? -1;
                w._settings.Save();
                w.UpdatePill();
            }
            else if (w._gripMoved) w.SaveDock();
            else w.ToggleDock();
            ObjC.Send(w._window, ObjC.Sel("makeFirstResponder:"), w._view);
        }
        catch (Exception ex) { Log.Error("Šipka menu: " + ex.Message); }
    }

    // přenos souborů ve stavové bublině
    private volatile string? _xfer;
    private long _lastXferBytes;

    /// <summary>Probíhající přenos souborů (null = žádný). Lze volat z libovolného vlákna.</summary>
    private void SetTransfer(string? label)
    {
        _xfer = label;
        MenuBarApp.RunOnMain(UpdatePill);
    }

    private static string FormatRate(long bytesPerSec) => bytesPerSec < 1024 * 1024
        ? $"{bytesPerSec / 1024} kB/s"
        : $"{bytesPerSec / 1024.0 / 1024:0.0} MB/s";

    private void ToggleKeys()
    {
        ReleaseKeys();
        _sendKeys = !_sendKeys;
        if (_keysCheck != IntPtr.Zero) ObjC.SetInt(_keysCheck, "setState:", _sendKeys ? 1 : 0);
        RefreshDock();
        UpdatePill();
        Toast(_sendKeys
            ? "Klávesy se posílají na druhý počítač (včetně zkratek s Cmd)."
            : "Klávesy se neposílají – zkratky zůstanou na tomto Macu.");
    }

    private IntPtr Btn(string symbol, string tip, Action a)
    {
        IntPtr b = UI.IconButton(symbol, tip, () =>
        {
            a();
            if (!_closed) ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _view);
        });
        _controls.Add(b);
        return b;
    }

    /// <summary>Oznámení dole uprostřed (jako na Windows); kliknutí propouští na druhý počítač.</summary>
    private void BuildToast(double w, double h)
    {
        _toast = UI.Glass(new CGRect((w - 520) / 2, 24, 520, 40), 13, behindWindow: false, cornerRadius: 12, passThrough: true);
        UI.Autoresize(_toast, 1 | 4 | 32);
        // tenký ukazatel průběhu u spodního okraje oznámení
        _toastBar = ObjC.NewWithFrame("NSView", new CGRect(0, 0, 0, 3));
        UI.Rounded(_toastBar, 1.5, UI.Accent);
        ObjC.SetBool(_toastBar, "setHidden:", true);
        UI.Add(_toast, _toastBar);
        _toastLabel = UI.Label("", 13, UI.WeightMedium, UI.Text);
        ObjC.SetInt(_toastLabel, "setAlignment:", 2);
        ObjC.SetInt(_toastLabel, "setLineBreakMode:", 4); // zkrátit uprostřed
        UI.Frame(_toastLabel, 14, 11, 492, 18);
        UI.Autoresize(_toastLabel, 2);
        UI.Add(_toast, _toastLabel);
        ObjC.SetBool(_toast, "setHidden:", true);
        UI.Add(_content, _toast);
    }

    private IntPtr _toastBar;

    /// <summary>Krátké oznámení dole v okně. progress 0–100 = ukazatel průběhu. Volat z libovolného vlákna.</summary>
    private void Toast(string text, bool error = false, bool sticky = false, int progress = -1)
    {
        MenuBarApp.RunOnMain(() =>
        {
            if (_closed) return;
            UI.SetText(_toastLabel, text);
            ObjC.Set(_toastLabel, "setTextColor:", error ? UI.Danger : UI.Text);
            bool bar = progress >= 0 && !error;
            ObjC.SetBool(_toastBar, "setHidden:", !bar);
            if (bar)
            {
                double tw = ObjC.Rect(_toast, "frame").W;
                UI.Frame(_toastBar, 0, 0, tw * Math.Clamp(progress, 0, 100) / 100.0, 3);
                UI.Rounded(_toastBar, 1.5, UI.Accent);
            }
            ObjC.SetBool(_toast, "setHidden:", false);
            _toastUntil = sticky ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(error ? 6 : 3.5);
        });
    }

    private void HideToast() => MenuBarApp.RunOnMain(() => { if (!_closed) _toastUntil = DateTime.UtcNow; });

    private void Tick()
    {
        if (_closed) return;
        if (_previewFor >= 0)
        {
            // myš odjela z okna (mouseMoved: pak nechodí) → schovat náhled obrazovky
            var mp = ObjC.SendPointObjRetPoint(_content, ObjC.Sel("convertPoint:fromView:"),
                ObjC.SendRetPoint(_window, ObjC.Sel("mouseLocationOutsideOfEventStream")), IntPtr.Zero);
            if (!OverDock(mp)) HidePreview();
        }
        int frames = Interlocked.Exchange(ref _frames, 0);
        long bytes = Interlocked.Exchange(ref _bytes, 0);
        _lastFps = frames;
        _lastXferBytes = _files.TakeBytes();
        _lastBytes = bytes;
        UpdatePill();
        RefreshDock();
        FlushTerminal();

        var ts = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(ts, Stopwatch.GetTimestamp());
        _client.Post(MsgType.Ping, ts);

        if (DateTime.UtcNow > _toastUntil) ObjC.SetBool(_toast, "setHidden:", true);
    }

    // ---------------- spojení ----------------

    // ---------------- automatické sdílení schránky ----------------

    private IntPtr _clipTimer;
    private string? _clipLast;   // text, který mají obě strany stejný
    private long _pbCount = -1;

    private static long PasteboardCount() =>
        ObjC.SendRetInt(ObjC.Get(ObjC.Cls("NSPasteboard"), "generalPasteboard"), ObjC.Sel("changeCount"));

    private void StartClipSync()
    {
        _client.Post(MsgType.ClipboardWatch, new byte[] { 1 });
        _pbCount = -1; // první kontrola pošle aktuální schránku tohoto Macu
        if (_clipTimer == IntPtr.Zero) _clipTimer = UI.Timer(0.5, CheckLocalClipboard);
        CheckLocalClipboard();
    }

    private void StopClipSync()
    {
        UI.StopTimer(_clipTimer);
        _clipTimer = IntPtr.Zero;
        _client.Post(MsgType.ClipboardWatch, new byte[] { 0 });
    }

    /// <summary>Schránka tohoto Macu se změnila → poslat text druhému počítači.</summary>
    private void CheckLocalClipboard()
    {
        if (_closed) return;
        long n = PasteboardCount();
        if (n == _pbCount) return;
        _pbCount = n;
        string? text = UI.PasteboardText();
        if (string.IsNullOrEmpty(text) || text.Length > ClipboardText.MaxAutoLength || ClipboardText.Same(text, _clipLast)) return;
        _clipLast = text;
        _client.PostJson(MsgType.ClipboardSet, new ClipboardText(text, Auto: true));
    }

    /// <summary>Schránka druhého počítače se změnila → převzít do macOS.</summary>
    private void OnAutoClipboard(string text)
    {
        if (!_settings.ClipboardSync || ClipboardText.Same(text, _clipLast)) return;
        _clipLast = text;
        UI.SetPasteboardText(text.Replace("\r\n", "\n"));
        _pbCount = PasteboardCount(); // vlastní změnu neposílat zpátky
    }

    private void StartStream()
    {
        _sentMaxWidth = EffectiveMaxWidth();
        // Delta = posílat jen změněné části obrazu; šířka podle okna – víc pixelů, než je vidět, nemá smysl přenášet
        _client.PostJson(MsgType.StreamStart, new StreamStart(_displayIndex, _settings.Fps, _settings.Quality, _sentMaxWidth, Delta: true));
        _streaming = true;
    }

    private int _sentMaxWidth, _resizeToken;

    /// <summary>Kolik pixelů obraz v okně skutečně zabere (body × Retina), nejvýš podle zvolené kvality (0 = bez omezení).</summary>
    private int EffectiveMaxWidth()
    {
        if (_view == IntPtr.Zero) return _settings.MaxWidth;
        var b = ObjC.Rect(_view, "bounds");
        if (b.W < 200 || b.H < 150) return _settings.MaxWidth;
        double scale = Math.Max(1, ObjC.SendRetDouble(_window, ObjC.Sel("backingScaleFactor")));
        var (rw, rh) = RemoteSize();
        double fitted = Math.Min(b.W, b.H * (rw / rh)) * scale;
        int w = ((int)fitted + 31) / 32 * 32;
        return _settings.MaxWidth > 0 ? Math.Min(_settings.MaxWidth, w) : w;
    }

    /// <summary>Po změně velikosti okna (s prodlevou) poslat nové rozlišení, pokud se výrazně liší.</summary>
    private void ScheduleStreamResize()
    {
        int token = ++_resizeToken;
        UI.After(700, () =>
        {
            if (_closed || token != _resizeToken || !_streaming) return;
            int want = EffectiveMaxWidth();
            if (_sentMaxWidth == 0 || Math.Abs(want - _sentMaxWidth) > _sentMaxWidth * 0.05) StartStream();
        });
    }

    public void Disconnect()
    {
        if (_closed) return;
        _closing = true;
        ObjC.Send(_window, ObjC.Sel("close"));
    }

    private void OnDisconnected(string reason)
    {
        MenuBarApp.RunOnMain(() =>
        {
            if (_closed || _closing) return;
            _closing = true;
            Log.Warn($"Spojení s {Title} přerušeno: {reason}");
            UI.Alert($"Spojení s {Title} bylo přerušeno", reason, new[] { "OK" }, warning: true);
            ObjC.Send(_window, ObjC.Sel("close"));
        });
    }

    public void WillClose()
    {
        // poloha menu a stavové bubliny – i pro příští připojení
        _settings.DockOpen = _dockOpen;
        _settings.DockPosition = _dockPos;
        _settings.PillX = _pillPos?.X ?? -1;
        _settings.PillY = _pillPos?.Y ?? -1;
        _settings.Save();
        _closed = true;
        ReleaseKeys();
        try { _client.Post(MsgType.StreamStop); } catch { }
        _client.MessageReceived -= OnMessage;
        _client.Disconnected -= OnDisconnected;
        _client.Dispose();
        UI.StopTimer(_timer);
        UI.StopTimer(_clipTimer);
        foreach (var c in _controls) UI.ForgetAction(c);
        ByView.Remove(_view);
        ByHandle.Remove(_handleGrip);
        ByHandle.Remove(_pillGrip);
        Open.Remove(this);
        _dropTargetWaiter?.TrySetCanceled();
        _dragFilesWaiter?.TrySetCanceled();

        IntPtr img = Interlocked.Exchange(ref _readyImage, IntPtr.Zero);
        if (img != IntPtr.Zero) Native.CGImageRelease(img);
        foreach (var t in _thumbs.Values) Native.CGImageRelease(t.Image);
        _thumbs.Clear();
        IntPtr window = _window, cursor = _cursor;
        UI.After(1000, () =>
        {
            // uvolnit až po dokončení zavírání (AppKit může okno ještě chvíli používat)
            if (_image != IntPtr.Zero) { Native.CGImageRelease(_image); _image = IntPtr.Zero; }
            if (cursor != IntPtr.Zero) ObjC.Send(cursor, ObjC.Sel("release"));
            ObjC.Send(window, ObjC.Sel("release"));
        });
        MenuBarApp.RefreshIcon();
        Log.Info($"Odpojeno od {Title}");
    }

    public void DidResignKey()
    {
        ReleaseKeys();
        UpdatePill();
    }

    public void DidResize()
    {
        ApplyCursor();
        LayoutDock();
        LayoutPanel();
        ScheduleStreamResize();
    }

    public void DidBecomeKey() => UpdatePill();

    // ---------------- zprávy ze serveru (vlákno čtení) ----------------

    private void OnMessage(Message m)
    {
        if (_files.Handle(m)) return;
        switch (m.Type)
        {
            case MsgType.Frame:
                HandleFrame(m.Payload);
                break;
            case MsgType.FramePatch:
                HandlePatch(m.Payload);
                break;
            case MsgType.Pong:
                if (m.Payload.Length == 8)
                    _pingMs = (Stopwatch.GetTimestamp() - BinaryPrimitives.ReadInt64LittleEndian(m.Payload)) * 1000.0 / Stopwatch.Frequency;
                break;
            case MsgType.Thumb:
                OnThumb(m.Payload);
                break;
            case MsgType.CursorShape:
            {
                try
                {
                    var shape = Bin.ReadCursorShape(m.Payload);
                    MenuBarApp.RunOnMain(() => { if (!_closed) { _cursorShape = shape; ApplyCursor(); } });
                }
                catch { /* poškozený tvar kurzoru ignorovat */ }
                break;
            }
            case MsgType.DropTargetInfo:
                Interlocked.Exchange(ref _dropTargetWaiter, null)?.TrySetResult(m.Json<DropTarget>());
                break;
            case MsgType.DragFiles:
                Interlocked.Exchange(ref _dragFilesWaiter, null)?.TrySetResult(m.Json<DragFiles>());
                break;
            case MsgType.ClipboardData:
            {
                var clip = m.Json<ClipboardText>();
                string text = clip.Text;
                if (clip.Auto)
                {
                    MenuBarApp.RunOnMain(() => { if (!_closed) OnAutoClipboard(text); });
                    break;
                }
                MenuBarApp.RunOnMain(() =>
                {
                    UI.SetPasteboardText(text);
                    Toast(text.Length == 0 ? "Schránka druhého počítače je prázdná." : $"Schránka načtena ({text.Length} znaků) – vložte ji Cmd+V.");
                });
                break;
            }
            case MsgType.ActionResult:
            {
                var r = m.Json<ActionResult>();
                if (r.Name.StartsWith("volume_")) MenuBarApp.RunOnMain(() => { if (!_closed) OnVolume(r); });
                if (!r.Ok) Toast($"Akce {r.Name} selhala: {r.Output}", error: true);
                break;
            }
            case MsgType.FileOpResult:
            {
                var r = m.Json<FileOpResult>();
                if (!r.Ok) Toast($"{Path.GetFileName(r.Path)}: {r.Message}", error: true);
                MenuBarApp.RunOnMain(() => { if (!_closed) OnFileOpDone(r); });
                break;
            }
            case MsgType.DirListing:
            {
                var l = m.Json<DirListing>();
                MenuBarApp.RunOnMain(() => { if (!_closed) OnDirListing(l); });
                break;
            }
            case MsgType.ExecOutput:
            {
                var o = m.Json<ExecOutput>();
                AppendTerm(o.Text + "\n", o.IsError ? 3 : 0);
                break;
            }
            case MsgType.ExecExit:
            {
                var e = m.Json<ExecExit>();
                MenuBarApp.RunOnMain(() => { if (!_closed) OnExecExit(e); });
                break;
            }
            case MsgType.Error:
                Toast("Chyba serveru: " + m.Json<ErrorInfo>().Message, error: true);
                break;
        }
    }

    /// <summary>Snímek hned potvrdit (server smí posílat další) a dekódovat na pozadí – vždy jen ten nejnovější.</summary>
    private void HandleFrame(byte[] jpeg)
    {
        Interlocked.Add(ref _bytes, jpeg.Length);
        _client.Post(MsgType.FrameAck);
        if (!_streaming || _closed) return;
        Interlocked.Exchange(ref _pendingJpeg, jpeg);
        if (Interlocked.CompareExchange(ref _decoding, 1, 0) == 0)
            _ = Task.Run(DecodeLoop);
    }

    private void DecodeLoop()
    {
        while (true)
        {
            var jpeg = Interlocked.Exchange(ref _pendingJpeg, null);
            if (jpeg == null)
            {
                Volatile.Write(ref _decoding, 0);
                if (Volatile.Read(ref _pendingJpeg) == null || Interlocked.CompareExchange(ref _decoding, 1, 0) != 0) return;
                continue;
            }
            IntPtr img;
            try { img = ClientNative.DecodeJpeg(jpeg); }
            catch (Exception ex) { Log.Warn("Dekódování snímku: " + ex.Message); continue; }
            if (img == IntPtr.Zero) continue;
            Interlocked.Increment(ref _frames);
            IntPtr old = Interlocked.Exchange(ref _readyImage, img);
            if (old != IntPtr.Zero) Native.CGImageRelease(old);
            if (Interlocked.Exchange(ref _presentQueued, 1) == 0)
                MenuBarApp.RunOnMain(Present);
        }
    }

    // ---------------- režim „jen změny“: záplaty popořadě do vlastního plátna ----------------

    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _patches = new();
    private int _patchWorker;
    private IntPtr _canvas;         // CGBitmapContext s celým obrazem (jen vlákno záplat)
    private int _canvasW, _canvasH;

    /// <summary>Vlákno čtení: záplatu zařadit; potvrdí se až po vykreslení (server tak nepředběhne klienta).</summary>
    private void HandlePatch(byte[] payload)
    {
        Interlocked.Add(ref _bytes, payload.Length);
        _patches.Enqueue(payload);
        if (Interlocked.CompareExchange(ref _patchWorker, 1, 0) == 0)
            _ = Task.Run(PatchLoop);
    }

    private void PatchLoop()
    {
        try
        {
            while (_patches.TryDequeue(out var payload))
            {
                try { if (_streaming && !_closed) ApplyPatch(payload); }
                catch (Exception ex) { Log.Warn("Záplata obrazu: " + ex.Message); }
                if (!_closed) _client.Post(MsgType.FrameAck);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _patchWorker, 0);
            if (!_patches.IsEmpty && Interlocked.CompareExchange(ref _patchWorker, 1, 0) == 0)
                _ = Task.Run(PatchLoop);
            if (_closed) ReleaseCanvas();
        }
    }

    private void ApplyPatch(byte[] payload)
    {
        var (w, h, reset, rects) = Bin.ReadFramePatch(payload);
        if (_canvas == IntPtr.Zero || reset || w != _canvasW || h != _canvasH)
        {
            ReleaseCanvas();
            IntPtr cs = Native.CGColorSpaceCreateDeviceRGB();
            _canvas = Native.CGBitmapContextCreate(IntPtr.Zero, (nuint)w, (nuint)h, 8, 0, cs, 6 | (2 << 12));
            Native.CGColorSpaceRelease(cs);
            if (_canvas == IntPtr.Zero) return;
            _canvasW = w;
            _canvasH = h;
        }
        foreach (var r in rects)
        {
            IntPtr img = ClientNative.DecodeJpeg(r.Jpeg.ToArray());
            if (img == IntPtr.Zero) continue;
            // CoreGraphics má počátek vlevo dole
            Native.CGContextDrawImage(_canvas, new CGRect(r.X, h - r.Y - r.H, r.W, r.H), img);
            Native.CGImageRelease(img);
        }
        IntPtr frame = Native.CGBitmapContextCreateImage(_canvas);
        if (frame == IntPtr.Zero) return;
        Interlocked.Increment(ref _frames);
        IntPtr old = Interlocked.Exchange(ref _readyImage, frame);
        if (old != IntPtr.Zero) Native.CGImageRelease(old);
        if (Interlocked.Exchange(ref _presentQueued, 1) == 0)
            MenuBarApp.RunOnMain(Present);
    }

    private void ReleaseCanvas()
    {
        if (_canvas != IntPtr.Zero) Native.CGContextRelease(_canvas);
        _canvas = IntPtr.Zero;
    }

    private void Present()
    {
        Volatile.Write(ref _presentQueued, 0);
        IntPtr img = Interlocked.Exchange(ref _readyImage, IntPtr.Zero);
        if (img == IntPtr.Zero) return;
        if (_closed) { Native.CGImageRelease(img); return; }
        bool first = _image == IntPtr.Zero;
        IntPtr old = _image;
        _image = img;
        _imgW = Native.CGImageGetWidth(img);
        _imgH = Native.CGImageGetHeight(img);
        ObjC.SetBool(_view, "setNeedsDisplay:", true);
        if (old != IntPtr.Zero) Native.CGImageRelease(old);
        if (first || !ObjC.SendRetBool0(_placeholder, ObjC.Sel("isHidden"))) ObjC.SetBool(_placeholder, "setHidden:", true);
    }

    // ---------------- souřadnice ----------------

    /// <summary>Kde je obraz ve view (zachovaný poměr stran).</summary>
    private CGRect ImageRect()
    {
        var b = ObjC.Rect(_view, "bounds");
        double iw = _imgW, ih = _imgH;
        if (iw <= 0 || ih <= 0) (iw, ih) = RemoteSize();
        double s = Math.Min(b.W / iw, b.H / ih);
        double w = iw * s, h = ih * s;
        return new CGRect(b.X + (b.W - w) / 2, b.Y + (b.H - h) / 2, w, h);
    }

    private CGPoint LocalPoint(IntPtr ev)
    {
        var p = ObjC.SendRetPoint(ev, ObjC.Sel("locationInWindow"));
        return ObjC.SendPointObjRetPoint(_view, ObjC.Sel("convertPoint:fromView:"), p, IntPtr.Zero);
    }

    /// <summary>Bod ve view → 0..1 v obrazu (y shora).</summary>
    private (float X, float Y, bool Inside) Normalize(CGPoint p)
    {
        var r = ImageRect();
        double x = (p.X - r.X) / r.W, y = 1 - (p.Y - r.Y) / r.H;
        bool inside = x >= 0 && x <= 1 && y >= 0 && y <= 1;
        return ((float)Math.Clamp(x, 0, 1), (float)Math.Clamp(y, 0, 1), inside);
    }

    /// <summary>Bod je nad menu nebo šipkou (tam se myš na druhý počítač neposílá).</summary>
    private bool OverDock(CGPoint viewPoint)
    {
        if (_handle != IntPtr.Zero && ObjC.Rect(_handle, "frame").Contains(viewPoint)) return true;
        if (_pill != IntPtr.Zero && !ObjC.SendRetBool0(_pill, ObjC.Sel("isHidden")) && ObjC.Rect(_pill, "frame").Contains(viewPoint)) return true;
        if (_panel != IntPtr.Zero && _open != PanelKind.None && ObjC.Rect(_panel, "frame").Contains(viewPoint)) return true;
        return _dockOpen && _dock != IntPtr.Zero && ObjC.Rect(_dock, "frame").Contains(viewPoint);
    }

    // ---------------- akce doku ----------------

    private void RunAction(string name, string? arg = null) => _client.PostJson(MsgType.Action, new ActionRequest(name, arg));

    private void SendClipboard()
    {
        string? text = UI.PasteboardText();
        if (string.IsNullOrEmpty(text)) { Toast("Schránka tohoto Macu neobsahuje text."); return; }
        _client.PostJson(MsgType.ClipboardSet, new ClipboardText(text));
        Toast($"Schránka odeslána ({text.Length} znaků) – na druhém počítači ji vložte.");
    }

    private void TypeClipboard()
    {
        string? text = UI.PasteboardText();
        if (string.IsNullOrEmpty(text)) { Toast("Schránka tohoto Macu neobsahuje text."); return; }
        ReleaseKeys();
        _client.Post(MsgType.Text, Bin.Utf8(text));
        Toast($"Píšu {text.Length} znaků…");
    }

    private void ToggleSwap()
    {
        _settings.SwapCmdCtrl = !_settings.SwapCmdCtrl;
        _settings.Save();
        ReleaseKeys();
        UpdateSwapButton();
        Toast(_settings.SwapCmdCtrl
            ? "Cmd funguje jako Ctrl (Cmd+C, Cmd+V, Cmd+Z…). Ctrl je klávesa Windows."
            : "Klávesy beze změny: Cmd = klávesa Windows, Ctrl = Ctrl.");
    }

    private void UpdateSwapButton()
    {
        if (_swapButton == IntPtr.Zero) return;
        ObjC.Set(_swapButton, "setToolTip:", ObjC.Str(_settings.SwapCmdCtrl
            ? "Cmd = Ctrl (zapnuto) – kliknutím vypnete"
            : "Cmd = klávesa Windows – kliknutím přepnete na Cmd = Ctrl"));
        if (ObjC.RespondsTo(_swapButton, "setContentTintColor:"))
            ObjC.Set(_swapButton, "setContentTintColor:", _settings.SwapCmdCtrl ? UI.Accent : UI.Text);
    }

    private void CycleFps()
    {
        int i = Array.IndexOf(FpsOptions, _settings.Fps);
        _settings.Fps = FpsOptions[(i + 1) % FpsOptions.Length];
        _settings.Save();
        UpdateFpsButton();
        StartStream();
        Toast($"Snímková frekvence: až {_settings.Fps} fps" +
              (_settings.Fps > 60 ? " (skutečná rychlost závisí na síti a výkonu druhého počítače)" : ""));
    }

    private void UpdateFpsButton()
    {
        if (_fpsButton != IntPtr.Zero)
            ObjC.Set(_fpsButton, "setToolTip:", ObjC.Str($"Snímková frekvence: {_settings.Fps} fps – kliknutím změníte"));
    }

    // ---------------- kurzor ----------------

    /// <summary>Tvar kurzoru druhého počítače, zmenšený podle měřítka obrazu v okně.</summary>
    private void ApplyCursor()
    {
        if (_cursorShape is not { } c) return;
        double displayW = RemoteSize().W;
        double scale = displayW > 0 ? ImageRect().W / displayW : 1;
        scale = Math.Clamp(scale, 0.5, 1.5);

        IntPtr data = ClientNative.NSData(c.Png);
        IntPtr image = ObjC.Send(ObjC.Send(ObjC.Cls("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithData:"), data);
        if (image == IntPtr.Zero) return;
        double w = Math.Max(4, c.W * scale), h = Math.Max(4, c.H * scale);
        ObjC.SendSize(image, ObjC.Sel("setSize:"), new CGSize(w, h));
        IntPtr cursor = ObjC.SendObjPoint(ObjC.Send(ObjC.Cls("NSCursor"), ObjC.Sel("alloc")), ObjC.Sel("initWithImage:hotSpot:"),
            image, new CGPoint(Math.Clamp(c.HotX * scale, 0, w - 1), Math.Clamp(c.HotY * scale, 0, h - 1)));
        ObjC.Send(image, ObjC.Sel("release"));
        if (cursor == IntPtr.Zero) return;
        IntPtr old = _cursor;
        _cursor = cursor;
        if (old != IntPtr.Zero) ObjC.Send(old, ObjC.Sel("release"));

        var p = ObjC.SendPointObjRetPoint(_view, ObjC.Sel("convertPoint:fromView:"),
            ObjC.SendRetPoint(_window, ObjC.Sel("mouseLocationOutsideOfEventStream")), IntPtr.Zero);
        if (ObjC.SendRetBool0(_window, ObjC.Sel("isKeyWindow")) && Normalize(p).Inside && !OverDock(p))
            ObjC.Send(_cursor, ObjC.Sel("set"));
    }

    private void UpdateLocalCursor(CGPoint p)
    {
        bool remote = Normalize(p).Inside && !OverDock(p);
        IntPtr c = remote && _cursor != IntPtr.Zero ? _cursor : ObjC.Get(ObjC.Cls("NSCursor"), "arrowCursor");
        ObjC.Send(c, ObjC.Sel("set"));
    }
}
