using System.Buffers.Binary;
using System.Diagnostics;
using RemoteScreen.Client.UI;
using RemoteScreen.Shared;
using Msg = RemoteScreen.Shared.Message;

namespace RemoteScreen.Client;

/// <summary>Hlavní okno v návrhu C: obraz druhého počítače přes celé okno, vysouvací menu vpravo,
/// stavová pilulka vpravo nahoře a plovoucí panely vlevo (Soubory, Terminál, Ovládání, Obraz).</summary>
public sealed partial class MainForm : Form
{
    private readonly RemoteClient _client;
    private readonly ClientSettings _settings;
    /// <summary>Nahrávání souborů (společný kód s klientem na Macu).</summary>
    private readonly FileTransfers _files;

    private enum PanelKind { None, Files, Terminal, Control, Settings }

    private readonly SidePanel _side = new();
    private PanelKind _open = PanelKind.None;
    private readonly Panel _pageTerminal = new() { Dock = DockStyle.Fill, Padding = new Padding(4) };
    private readonly Panel _pageFiles = new() { Dock = DockStyle.Fill, Padding = new Padding(4) };
    private readonly Panel _pageControl = new() { Dock = DockStyle.Fill };
    private readonly Panel _pageSettings = new() { Dock = DockStyle.Fill };

    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 1000 };

    private int _frames;
    private long _bytes;
    private double _pingMs = -1;
    private bool _disconnected;
    /// <summary>Ovládaný počítač je Windows (klávesy bez prohození Ctrl/Cmd, cesty s \\).</summary>
    private readonly bool _targetWindows;
    private readonly string _targetName;

    /// <summary>true = po zavření okna znovu ukázat přihlášení (spojení spadlo nebo uživatel zvolil Odpojit).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ShowConnectAfterClose { get; private set; }

    public MainForm(RemoteClient client, ClientSettings settings)
    {
        _client = client;
        _settings = settings;
        _files = new FileTransfers(client);

        var info = client.Info;
        _targetWindows = string.Equals(info.Platform, "windows", StringComparison.OrdinalIgnoreCase);
        _targetName = info.HostName ?? client.Host;
        Text = $"{_targetName} – RemoteScreen";
        Icon = Program.AppIcon;
        Size = new Size(1320, 860);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;

        foreach (var p in new[] { _pageTerminal, _pageFiles, _pageControl, _pageSettings }) p.BackColor = Color.Transparent;

        BuildDesktopPage();
        BuildTerminalPage();
        BuildFilesPage();
        BuildControlPage();
        BuildDock();

        _side.Visible = false;
        _side.BackdropSource = area => _view.BlurredSnapshot(area);
        _side.BackdropVersion = () => _view.FrameVersion;
        _side.Closed += ClosePanel;
        Controls.Add(_view);
        Controls.Add(_side);
        _side.BringToFront();

        _statsTimer.Tick += (_, _) => UpdateStats();
        _resizeStream.Tick += (_, _) => OnViewResizedForStream();
        _client.MessageReceived += OnMessage;
        _client.Disconnected += OnDisconnected;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _client.Start();
        _statsTimer.Start();
        InitDesktop();
        InitTerminal();
        InitFiles();
        InitControl();
        UpdateStats();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutSide();
        // nové rozlišení obrazu podle velikosti okna (až se okno přestane měnit)
        if (IsHandleCreated) { _resizeStream.Stop(); _resizeStream.Start(); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _settings.DockOpen = _view.DockOpen;
        _settings.DockPosition = _view.DockPosition;
        _settings.StatusX = _view.StatusPosition?.X ?? -1;
        _settings.StatusY = _view.StatusPosition?.Y ?? -1;
        _settings.Save();
        _statsTimer.Stop();
        _clipTimer.Stop();
        DisposeDesktop();
        DisposeFiles();
        CleanupDragTemp();
        _client.MessageReceived -= OnMessage;
        _client.Disconnected -= OnDisconnected;
        _client.Dispose();
        base.OnFormClosing(e);
    }

    // ---------------- dok a panely ----------------

    private void BuildDock()
    {
        string target = _targetWindows ? "PC" : "Mac";
        _view.DockOpen = _settings.DockOpen;
        _view.DockPosition = _settings.DockPosition;
        if (_settings.StatusX >= 0 && _settings.StatusY >= 0) _view.StatusPosition = new PointF(_settings.StatusX, _settings.StatusY);
        _view.StatusMoved += () =>
        {
            var sp = _view.StatusPosition;
            _settings.StatusX = sp?.X ?? -1;
            _settings.StatusY = sp?.Y ?? -1;
            _settings.Save();
        };
        _view.DockStateChanged += () =>
        {
            _settings.DockOpen = _view.DockOpen;
            _settings.DockPosition = _view.DockPosition;
            _settings.Save();
        };
        _view.DockItems.AddRange(new[]
        {
            // Soubory a Terminál odebrány: soubory se přetahují myší, klávesy a schránka fungují samy
            new DockItem { Glyph = Glyph.Remote, Label = "Ovládání", Active = () => _open == PanelKind.Control, Click = () => TogglePanel(PanelKind.Control) },
            new DockItem { Glyph = Glyph.Settings, Label = "Obraz", Active = () => _open == PanelKind.Settings, Click = () => TogglePanel(PanelKind.Settings), SeparatorBefore = true },
            new DockItem { Glyph = Glyph.Lock, Label = "Zámek", Click = () => { _client.PostJson(MsgType.Action, new ActionRequest("lock")); _view.ShowToast($"Zamykám {target}…"); } },
            new DockItem { Glyph = Glyph.FullScreen, Label = "Celá obrazovka", Active = () => _fullscreen, Click = ToggleFullscreen },
            new DockItem { Glyph = Glyph.Cancel, Label = "Odpojit", Danger = true, SeparatorBefore = true, Click = () => { ShowConnectAfterClose = true; Close(); } },
        });

        // víc obrazovek na druhém počítači → v menu tlačítko pro každou (hned pod „Obraz“),
        // po najetí myší malý náhled té obrazovky
        var displays = _client.Info.Displays ?? Array.Empty<DisplayInfo>();
        if (displays.Length > 1)
        {
            for (int i = 0; i < displays.Length; i++)
            {
                int idx = i;
                var d = displays[i];
                _view.DockItems.Insert(2 + i, new DockItem
                {
                    Glyph = Glyph.Desktop,
                    Label = $"Monitor {i + 1}",
                    Active = () => _cbDisplay.SelectedIndex == idx,
                    Click = () => { if (_cbDisplay.SelectedIndex != idx && idx < _cbDisplay.Items.Count) _cbDisplay.SelectedIndex = idx; },
                    Hover = () => RequestThumb(d.Index),
                    Preview = () => _thumbs.TryGetValue(d.Index, out var t) ? t.Image : null,
                    PreviewCaption = () => $"Monitor {idx + 1} · {d.Width:0}×{d.Height:0}{(d.IsMain ? " · hlavní" : "")}",
                });
            }
        }
    }

    private readonly Dictionary<int, (Image Image, DateTime At)> _thumbs = new();
    private readonly Dictionary<int, DateTime> _thumbAsked = new();

    /// <summary>Vyžádá náhled obrazovky (nejvýš jednou za 1,5 s pro každou).</summary>
    private void RequestThumb(int displayIndex)
    {
        if (_thumbAsked.TryGetValue(displayIndex, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(1.5)) return;
        _thumbAsked[displayIndex] = DateTime.UtcNow;
        _client.Post(MsgType.ThumbGet, new[] { (byte)displayIndex });
    }

    private void OnThumb(byte[] payload)
    {
        if (payload.Length < 2) return;
        try
        {
            using var ms = new MemoryStream(payload, 1, payload.Length - 1, writable: false);
            using var img = Image.FromStream(ms);
            var bmp = new Bitmap(img);
            if (_thumbs.TryGetValue(payload[0], out var old)) old.Image.Dispose();
            _thumbs[payload[0]] = (bmp, DateTime.UtcNow);
            _view.InvalidatePreview();
        }
        catch { /* poškozený náhled ignorovat */ }
    }

    private void UpdateMonitorItem()
    {
        _view.Invalidate();
        var displays = _client.Info.Displays ?? Array.Empty<DisplayInfo>();
        int i = Math.Max(0, _cbDisplay.SelectedIndex);
        if (displays.Length > 1 && i < displays.Length)
        {
            var d = displays[i];
            _view.ShowToast($"Monitor {i + 1} z {displays.Length} · {d.Width:0}×{d.Height:0}{(d.IsMain ? " (hlavní)" : "")}");
        }
    }

    private void ToggleKeyboard()
    {
        _chkKeyboard.Checked = !_chkKeyboard.Checked;
        _view.ShowToast(_chkKeyboard.Checked
            ? "Klávesnice se posílá na druhý počítač (po kliknutí do obrazu) · pravý Ctrl + K uvolní"
            : "Klávesnice se nezachytává – Win, Alt+Tab apod. zůstanou ve Windows", Theme.Accent);
        _view.Focus();
    }

    private void TogglePanel(PanelKind kind)
    {
        if (_open == kind) ClosePanel();
        else OpenPanel(kind);
    }

    private void OpenPanel(PanelKind kind)
    {
        var (page, title) = kind switch
        {
            PanelKind.Files => (_pageFiles, "Soubory"),
            PanelKind.Terminal => (_pageTerminal, "Terminál"),
            PanelKind.Control => (_pageControl, "Ovládání"),
            _ => (_pageSettings, "Obraz a klávesnice"),
        };
        _side.ShowPage(page);
        _side.Title = title;
        _open = kind;
        LayoutSide();
        _side.Visible = true;
        _side.BringToFront();
        _view.DockPinned = true;
        _view.Invalidate();
        ReleaseKeys();
        if (kind == PanelKind.Files && _entries.Length == 0) RequestList(_remotePath.Length > 0 ? _remotePath : "~");
        if (kind == PanelKind.Terminal) _termIn.Focus();
        UpdateKeyboardLabel();
    }

    private void ClosePanel()
    {
        _side.Visible = false;
        _open = PanelKind.None;
        _view.DockPinned = false;
        _view.Invalidate();
        _view.Focus();
        UpdateKeyboardLabel();
    }

    private void LayoutSide()
    {
        int want = _open switch
        {
            PanelKind.Files => 540,
            PanelKind.Terminal => 580,
            PanelKind.Control => 440,
            _ => 360,
        };
        int w = Math.Max(300, Math.Min(want, ClientSize.Width - 28));
        int h = Math.Max(200, ClientSize.Height - 28);
        _side.SetBounds(14, 14, w, h);
    }

    // ---------------- příjem zpráv ----------------

    /// <summary>Běží na vlákně čtení. Obraz a soubory se zpracují hned, zbytek na UI vlákně.</summary>
    private void OnMessage(Msg m)
    {
        if (_files.Handle(m)) return; // potvrzení nahrávání a přenosy společného kódu
        switch (m.Type)
        {
            case MsgType.Frame:
                HandleFrame(m.Payload);
                return;
            case MsgType.FramePatch:
                HandlePatch(m.Payload);
                return;
            case MsgType.Pong:
                if (m.Payload.Length == 8)
                {
                    long sent = BinaryPrimitives.ReadInt64LittleEndian(m.Payload);
                    _pingMs = (Stopwatch.GetTimestamp() - sent) * 1000.0 / Stopwatch.Frequency;
                }
                return;
            case MsgType.FileStart:
                OnFileStart(m.Json<FileStart>());
                return;
            case MsgType.FileChunk:
                OnFileChunk(m.Payload);
                return;
            case MsgType.FileEnd:
                OnFileEnd(m.Json<FileEnd>());
                return;
            case MsgType.ExecOutput:
            {
                var o = m.Json<ExecOutput>();
                EnqueueTerminal(o.Text + "\n", o.IsError ? TermError : TermText);
                return;
            }
            case MsgType.DropTargetInfo:
                Interlocked.Exchange(ref _dropTargetWaiter, null)?.TrySetResult(m.Json<DropTarget>());
                return;
            case MsgType.Thumb:
                Ui(() => OnThumb(m.Payload)); // slovník náhledů čte i kreslení – jen na UI vlákně
                return;
            case MsgType.CursorShape:
                try
                {
                    var (hx, hy, w, h, png) = Bin.ReadCursorShape(m.Payload);
                    using var ms = new MemoryStream(png);
                    using var img = Image.FromStream(ms);
                    _view.SetRemoteCursor(new Bitmap(img), hx, hy, w, h);
                }
                catch { /* poškozený tvar kurzoru ignorovat */ }
                return;
            case MsgType.DragFiles:
                Interlocked.Exchange(ref _dragFilesWaiter, null)?.TrySetResult(m.Json<DragFiles>());
                return;
        }

        Ui(() =>
        {
            switch (m.Type)
            {
                case MsgType.ExecExit: OnExecExit(m.Json<ExecExit>()); break;
                case MsgType.DirListing: OnDirListing(m.Json<DirListing>()); break;
                case MsgType.FileOpResult: OnFileOpResult(m.Json<FileOpResult>()); break;
                case MsgType.ActionResult: OnActionResult(m.Json<ActionResult>()); break;
                case MsgType.ClipboardData: OnClipboardData(m.Json<ClipboardText>()); break;
                case MsgType.Error:
                {
                    var err = m.Json<ErrorInfo>();
                    LogControl("Chyba serveru: " + err.Message);
                    break;
                }
            }
        });
    }

    private void OnDisconnected(string reason)
    {
        Ui(() =>
        {
            if (_disconnected || IsDisposed) return;
            _disconnected = true;
            _view.StatusOk = false;
            _view.Invalidate();
            if (!Disposing && Visible)
            {
                MessageBox.Show(this, $"Spojení s {_targetName} bylo přerušeno.\n\n" + reason, "RemoteScreen",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ShowConnectAfterClose = true;
                Close();
            }
        });
    }

    private int _lastFps;
    private long _lastKbps;

    // přenos souborů ve stavové bublině
    private string? _xfer;
    private long _xferBytes, _lastXferBytes;

    /// <summary>Probíhající přenos souborů (null = žádný) – ukáže se ve stavové bubline i s rychlostí.</summary>
    private void SetTransfer(string? label)
    {
        if (_xfer == label) return;
        _xfer = label;
        RefreshStatus(); // na celé obrazovce se zasunutým menu zůstává bublina schovaná – průběh je v oznámení dole
    }

    private static string FormatRate(long bytesPerSec) => bytesPerSec switch
    {
        < 1024 * 1024 => $"{bytesPerSec / 1024} kB/s",
        _ => $"{bytesPerSec / 1024.0 / 1024:0.0} MB/s",
    };

    private void UpdateStats()
    {
        _lastFps = Interlocked.Exchange(ref _frames, 0);
        _lastKbps = Interlocked.Exchange(ref _bytes, 0) / 1024;
        _lastXferBytes = Interlocked.Exchange(ref _xferBytes, 0) + _files.TakeBytes();
        RefreshStatus();

        var ts = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(ts, Stopwatch.GetTimestamp());
        _client.Post(MsgType.Ping, ts);
        FlushTerminal();
    }

    /// <summary>Text stavové pilulky vpravo nahoře.</summary>
    private void RefreshStatus()
    {
        string ping = _pingMs < 0 ? "–" : $"{_pingMs:0} ms";
        string kb = _hook != null && CaptureActive ? "  ·  ⌨ klávesnice" : "";
        if (_xfer != null && !_disconnected)
        {
            _view.StatusText = $"{_targetName} · {_xfer} · {FormatRate(_lastXferBytes)}";
            return;
        }
        _view.StatusText = _disconnected
            ? $"{_targetName} · odpojeno"
            : _streaming
                ? _lastFps == 0
                    ? $"{_targetName} · obraz beze změn · {ping}{kb}"
                    : $"{_targetName} · {_lastFps} fps · {ping} · {_lastKbps} kB/s{kb}"
                : $"{_targetName} · obraz zastaven · {ping}{kb}";
    }

    /// <summary>Bezpečné spuštění na UI vlákně z libovolného vlákna.</summary>
    private void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { } // zahrnuje i ObjectDisposedException
    }

    /// <summary>Lišta nástrojů nahoře na stránce.</summary>
    private static FlowLayoutPanel Toolbar() => new()
    {
        Dock = DockStyle.Top,
        AutoSize = true,
        WrapContents = true,
        Padding = new Padding(0, 0, 0, 6),
        BackColor = Color.Transparent,
    };
}
