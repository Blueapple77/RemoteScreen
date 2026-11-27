using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteScreen.Client.UI;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

public sealed partial class MainForm
{
    private static readonly (string Name, int Quality, int MaxWidth)[] Qualities =
    {
        ("Nízká – rychlá", 50, 1280),
        ("Střední", 70, 1920),
        ("Vysoká", 85, 2560),
        ("Plné rozlišení", 90, 0),
    };
    private static readonly int[] FpsOptions = { 15, 30, 60, 90, 120, 144 };

    private readonly RemoteView _view = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _cbDisplay = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }.Styled();
    private readonly ComboBox _cbQuality = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }.Styled();
    private readonly ComboBox _cbFps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }.Styled();
    private readonly FlatButton _btnStream = new() { Kind = ButtonKind.Primary, AutoFit = false };
    private readonly FlatButton _btnUploadDesktop = new() { Text = "Nahrát soubory…", Glyph = Glyph.Upload, Kind = ButtonKind.Secondary, AutoFit = false };
    private readonly ToggleSwitch _chkSwap = new() { Text = "Ctrl ↔ Cmd" };
    private readonly ToggleSwitch _chkKeyboard = new() { Text = "Zachytávat klávesnici" };

    private bool _streaming;
    private string tipFps = "";
    private byte[]? _pendingJpeg;
    private int _decoderRunning;
    private KeyboardHook? _hook;
    private bool _hostKeyDown;
    private readonly HashSet<ushort> _pressed = new();

    private bool _fullscreen;
    private FormWindowState _prevState;
    private FormBorderStyle _prevBorder;

    /// <summary>Panel „Obraz a klávesnice“: obraz, monitor, kvalita, snímková frekvence, klávesy, nahrání souborů.</summary>
    private void BuildDesktopPage()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true,
            BackColor = Color.Transparent, Padding = new Padding(2, 0, 2, 8),
        };
        const int w = 300;
        Control Caption(string text)
        {
            var l = new TLabel(text, dim: true) { Font = Theme.Small, Margin = new Padding(3, 12, 3, 2) };
            return l;
        }
        foreach (var c in new Control[] { _cbDisplay, _cbQuality, _cbFps }) { c.Width = w; c.Height = 30; }
        _btnStream.Width = w; _btnStream.Height = 38;
        _btnUploadDesktop.Width = w; _btnUploadDesktop.Height = 36;
        _btnUploadDesktop.Text = _targetWindows ? "Nahrát soubory na PC…" : "Nahrát soubory na Mac…";
        var hint = new Label
        {
            AutoSize = false, Width = w, Height = 120, Font = Theme.Small, ForeColor = Theme.TextDim, BackColor = Color.Transparent,
            Margin = new Padding(3, 14, 3, 3),
            Text = "Soubory stačí přetáhnout přímo do obrazu – a opačně je chytit v obrazu a vytáhnout ven z okna.\n\n" +
                   "Pravý Ctrl je hostitelská klávesa: + F celá obrazovka, + K uvolnit klávesnici, + Del okno Vynutit ukončení.",
        };
        flow.Controls.AddRange(new Control[]
        {
            _btnStream,
            Caption("Monitor"), _cbDisplay,
            Caption("Kvalita obrazu"), _cbQuality,
            Caption("Snímková frekvence (fps)"), _cbFps,
            Caption("Klávesnice"), _chkKeyboard, _chkSwap,
            Caption("Vzhled – barva zvýraznění"), AccentRow(),
            hint,
        });
        _pageSettings.Controls.Add(flow);

        foreach (var q in Qualities) _cbQuality.Items.Add(q.Name);
        foreach (var f in FpsOptions) _cbFps.Items.Add(f.ToString());
        _cbQuality.SelectedIndex = Math.Clamp(_settings.QualityIndex, 0, Qualities.Length - 1);
        _cbFps.SelectedIndex = Math.Clamp(_settings.FpsIndex, 0, FpsOptions.Length - 1);
        tipFps = "Skutečná frekvence závisí na rozlišení, kvalitě a obnovovací frekvenci monitoru Macu – víc, než zvládne monitor Macu (často 60 Hz), nemá smysl.";
        _chkSwap.Checked = _settings.SwapCtrlCmd;
        if (_targetWindows)
        {
            // Windows → Windows: klávesy 1:1, prohození Ctrl/Cmd nedává smysl
            _chkSwap.Visible = false;
            _view.DropHint = "Pusťte nad oknem Průzkumníka → uloží se do jeho složky  ·  nad aplikací → otevře se v ní  ·  jinde → na Plochu";
        }
        _chkKeyboard.Checked = _settings.CaptureKeyboard;

        var tip = new ToolTip();
        tip.SetToolTip(_chkSwap, "Levý Ctrl pošle Cmd a klávesa Win pošle Control – Ctrl+C / Ctrl+V pak funguje jako na Windows.");
        tip.SetToolTip(_chkKeyboard, "Po kliknutí do obrazu se na Mac posílají všechny klávesy včetně Win a Alt+Tab.\n" +
                                      "Pravý Ctrl je hostitelská klávesa: +F celá obrazovka, +K uvolnit klávesnici, +Del Vynutit ukončení.");
        tip.SetToolTip(_cbFps, tipFps);
        tip.SetToolTip(_btnUploadDesktop, "Soubory můžete také přetáhnout myší přímo do obrazu Macu.");
    }

    private void InitDesktop()
    {
        var displays = _client.Info.Displays ?? Array.Empty<DisplayInfo>();
        foreach (var d in displays)
            _cbDisplay.Items.Add($"{d.Index + 1}: {d.Width:0}×{d.Height:0}{(d.IsMain ? " (hlavní)" : "")}");
        if (_cbDisplay.Items.Count == 0) _cbDisplay.Items.Add("1: hlavní");
        int main = Array.FindIndex(displays, d => d.IsMain);
        _cbDisplay.SelectedIndex = Math.Max(0, main);

        _cbDisplay.SelectedIndexChanged += (_, _) => { if (_streaming) StartStream(); UpdateMonitorItem(); _view.Focus(); };
        _cbQuality.SelectedIndexChanged += (_, _) => { _settings.QualityIndex = _cbQuality.SelectedIndex; if (_streaming) StartStream(); _view.Focus(); };
        _cbFps.SelectedIndexChanged += (_, _) => { _settings.FpsIndex = _cbFps.SelectedIndex; if (_streaming) StartStream(); _view.Focus(); };
        _chkSwap.CheckedChanged += (_, _) => { ReleaseKeys(); _settings.SwapCtrlCmd = _chkSwap.Checked; };
        _chkKeyboard.CheckedChanged += (_, _) => { ReleaseKeys(); _settings.CaptureKeyboard = _chkKeyboard.Checked; UpdateKeyboardLabel(); };
        _btnStream.Click += (_, _) => { if (_streaming) StopStream(); else StartStream(); _view.Focus(); };
        _btnUploadDesktop.Click += (_, _) => UploadViaDialogToMac();

        _view.ControlledByTarget = () => global::RemoteScreen.Host.ServerHost.IsControlledFrom(_client.RemoteAddress);
        _view.MouseMoved += (x, y) => _client.Post(MsgType.MouseMove, Bin.MouseMove(x, y));
        _view.MouseButtonChanged += (b, down, clicks, x, y) => _client.Post(MsgType.MouseButton, Bin.MouseButton(b, down, clicks, x, y));
        _view.Scrolled += (dy, dx) => _client.Post(MsgType.MouseScroll, Bin.Scroll(dy, dx));
        _view.FilesDropped += (files, x, y) => _ = UploadDroppedAsync(files, x, y);
        _view.DragLeftView += () => _ = DragOutAsync();
        _view.GotFocus += (_, _) => UpdateKeyboardLabel();
        _view.LostFocus += (_, _) => { ReleaseKeys(); UpdateKeyboardLabel(); };
        Deactivate += (_, _) => { ReleaseKeys(); UpdateKeyboardLabel(); };
        Activated += (_, _) => UpdateKeyboardLabel();

        try
        {
            _hook = new KeyboardHook { Handler = OnHookKey };
        }
        catch (Exception ex)
        {
            _chkKeyboard.Enabled = false;
            _view.ShowToast("Zachytávání klávesnice nelze zapnout: " + ex.Message, Theme.Danger);
        }

        StartStream();
        _view.Focus();
        UpdateKeyboardLabel();
    }

    private void DisposeDesktop()
    {
        ReleaseKeys();
        _hook?.Dispose();
        _hook = null;
    }

    // ---------------- obraz ----------------

    private void StartStream()
    {
        var q = Qualities[Math.Max(0, _cbQuality.SelectedIndex)];
        int fps = FpsOptions[Math.Max(0, _cbFps.SelectedIndex)];
        int displayIndex = Math.Max(0, _cbDisplay.SelectedIndex);
        var displays = _client.Info.Displays ?? Array.Empty<DisplayInfo>();
        _view.DisplayWidthPt = displays.FirstOrDefault(d => d.Index == displayIndex)?.Width ?? 0;
        _sentMaxWidth = EffectiveMaxWidth(q.MaxWidth);
        // Delta = posílat jen změněné části obrazu; šířka podle okna – víc pixelů, než je vidět, nemá smysl přenášet
        _client.PostJson(MsgType.StreamStart, new StreamStart(displayIndex, fps, q.Quality, _sentMaxWidth, Delta: true));
        _streaming = true;
        _btnStream.Text = "Zastavit";
        _btnStream.Glyph = Glyph.Stop;
        _btnStream.Kind = ButtonKind.Secondary;
        _view.Placeholder = _targetWindows
            ? "Čekám na obraz…"
            : "Čekám na obraz…\n\nPokud obraz nepřichází, zkontrolujte v menu RemoteScreen v horní liště Macu oprávnění Nahrávání obrazovky.";
        _view.Invalidate();
    }

    private int _sentMaxWidth;
    private readonly System.Windows.Forms.Timer _resizeStream = new() { Interval = 700 };

    /// <summary>Šířka obrazu, kterou má smysl přenášet: kolik pixelů obraz v okně skutečně zabere
    /// (podle poměru stran druhé obrazovky), nejvýš podle zvolené kvality (0 = bez omezení).</summary>
    private int EffectiveMaxWidth(int presetMax)
    {
        var size = _view.ClientSize;
        if (size.Width < 200 || size.Height < 150) return presetMax;
        var displays = _client.Info.Displays ?? Array.Empty<DisplayInfo>();
        var d = displays.FirstOrDefault(x => x.Index == Math.Max(0, _cbDisplay.SelectedIndex)) ?? displays.FirstOrDefault();
        double aspect = d != null && d.Height > 0 ? d.Width / d.Height : 16.0 / 10;
        int fitted = (int)Math.Min(size.Width, size.Height * aspect);
        int w = (fitted + 31) / 32 * 32;
        return presetMax > 0 ? Math.Min(presetMax, w) : w;
    }

    /// <summary>Po změně velikosti okna (s prodlevou) poslat nové rozlišení, pokud se výrazně liší.</summary>
    private void OnViewResizedForStream()
    {
        _resizeStream.Stop();
        if (!_streaming) return;
        var q = Qualities[Math.Max(0, _cbQuality.SelectedIndex)];
        int want = EffectiveMaxWidth(q.MaxWidth);
        if (_sentMaxWidth == 0 || Math.Abs(want - _sentMaxWidth) > _sentMaxWidth * 0.05) StartStream();
    }

    private void StopStream()
    {
        _client.Post(MsgType.StreamStop);
        _streaming = false;
        _btnStream.Text = "Spustit obraz";
        _btnStream.Glyph = Glyph.Play;
        _btnStream.Kind = ButtonKind.Primary;
        _view.Placeholder = "Obraz je zastaven";
        _view.SetFrame(null);
    }

    /// <summary>Vlákno čtení: snímek hned potvrdit (server může posílat dál) a dekódovat na pozadí.
    /// Když dekódování nestíhá, přeskočí se starší snímky – vždy se ukáže ten nejnovější.</summary>
    private void HandleFrame(byte[] jpeg)
    {
        Interlocked.Add(ref _bytes, jpeg.Length);
        _client.Post(MsgType.FrameAck);
        if (!_streaming) return;
        Interlocked.Exchange(ref _pendingJpeg, jpeg);
        if (Interlocked.CompareExchange(ref _decoderRunning, 1, 0) == 0)
            _ = Task.Run(DecodeLoop);
    }

    // ---------------- režim „jen změny“: záplaty se musí použít všechny a popořadě ----------------

    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _patches = new();
    private int _patchWorker;

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
                try { if (_streaming) ApplyPatch(payload); }
                catch { /* poškozená záplata – obraz se opraví další změnou */ }
                _client.Post(MsgType.FrameAck);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _patchWorker, 0);
            if (!_patches.IsEmpty && Interlocked.CompareExchange(ref _patchWorker, 1, 0) == 0)
                _ = Task.Run(PatchLoop);
        }
    }

    private void ApplyPatch(byte[] payload)
    {
        var (w, h, reset, rects) = Bin.ReadFramePatch(payload);
        var images = new List<(Rectangle, Bitmap)>(rects.Count);
        try
        {
            foreach (var r in rects)
            {
                using var ms = new MemoryStream(r.Jpeg.Array!, r.Jpeg.Offset, r.Jpeg.Count, writable: false);
                using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
                images.Add((new Rectangle(r.X, r.Y, r.W, r.H), new Bitmap(img)));
            }
            _view.ApplyPatch(w, h, reset, images);
            Interlocked.Increment(ref _frames);
        }
        finally
        {
            foreach (var (_, bmp) in images) bmp.Dispose();
        }
    }

    private void DecodeLoop()
    {
        try
        {
            while (Interlocked.Exchange(ref _pendingJpeg, null) is byte[] jpeg)
                DecodeFrame(jpeg);
        }
        finally
        {
            Interlocked.Exchange(ref _decoderRunning, 0);
            // mezitím mohl přijít další snímek
            if (Volatile.Read(ref _pendingJpeg) != null && Interlocked.CompareExchange(ref _decoderRunning, 1, 0) == 0)
                _ = Task.Run(DecodeLoop);
        }
    }

    private void DecodeFrame(byte[] jpeg)
    {
        Interlocked.Increment(ref _frames);
        try
        {
            if (!_streaming) return;
            using var ms = new MemoryStream(jpeg);
            using var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
            var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage(img, new Rectangle(0, 0, img.Width, img.Height));
            }
            _view.SetFrame(bmp);
        }
        catch
        {
            // poškozený snímek přeskočit
        }
    }

    // ---------------- klávesnice ----------------

    private bool CaptureActive =>
        _chkKeyboard.Checked && _client.IsConnected && _view.Focused && ActiveForm == this;

    /// <summary>Výběr barvy zvýraznění – projeví se hned ve všech oknech a uloží se.</summary>
    private Control AccentRow()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        foreach (var (name, color) in Theme.AccentPresets)
        {
            var sw = new AccentSwatch(name, color);
            sw.Click += (_, _) =>
            {
                Theme.SetAccent(sw.Swatch);
                _settings.AccentColor = Theme.ToHex(sw.Swatch);
                _settings.Save();
                foreach (Form f in Application.OpenForms) f.Invalidate(true);
            };
            row.Controls.Add(sw);
        }
        return row;
    }

    private bool _lastCapture;

    /// <summary>Stav zachytávání klávesnice: zvýraznění v doku a symbol ⌨ ve stavové pilulce.</summary>
    private void UpdateKeyboardLabel()
    {
        bool active = _hook != null && CaptureActive;
        if (active == _lastCapture) { _view.Invalidate(); return; }
        _lastCapture = active;
        RefreshStatus();
        _view.Invalidate();
    }

    /// <summary>Volá se z hooku na UI vlákně. true = klávesu poslat na Mac a ve Windows ji spolknout.</summary>
    private bool OnHookKey(int scan, bool extended, bool up, int vk)
    {
        if (!CaptureActive) return false;

        // AltGr posílá falešný levý Ctrl se scan kódem 0x21D
        if ((scan & 0x200) != 0) return true;

        // pravý Ctrl = hostitelská klávesa, na Mac se neposílá
        if (vk == 0xA3 || (scan == 0x1D && extended))
        {
            _hostKeyDown = !up;
            return true;
        }
        if (_hostKeyDown)
        {
            if (!up)
            {
                switch (scan)
                {
                    case 0x21: BeginInvoke(new Action(ToggleFullscreen)); break; // F
                    case 0x25: // K
                        BeginInvoke(new Action(() => { _chkKeyboard.Checked = false; }));
                        break;
                    case 0x53 when extended: // Delete
                        _client.PostJson(MsgType.Action, new ActionRequest("force_quit"));
                        break;
                }
            }
            return true;
        }

        // multimediální klávesy → akce na Macu
        string? action = vk switch
        {
            0xAD => "volume_mute",
            0xAE => "volume_down",
            0xAF => "volume_up",
            0xB3 => "media_playpause",
            0xB0 => "media_next",
            0xB1 => "media_prev",
            _ => null,
        };
        if (action != null)
        {
            if (!up) _client.PostJson(MsgType.Action, new ActionRequest(action));
            return true;
        }

        bool swap = _chkSwap.Checked && !_targetWindows;
        // modifikátory podle virtuálního kódu – scan kód/příznak „extended“ se u nich liší podle klávesnice
        ushort? code = vk switch
        {
            0xA0 => (ushort)0x38,                                   // levý Shift
            0xA1 => (ushort)0x3C,                                   // pravý Shift
            0x10 => extended ? (ushort)0x3C : (ushort)0x38,         // obecný Shift (některé ovladače)
            0xA2 => swap ? (ushort)0x37 : (ushort)0x3B,             // levý Ctrl
            0xA4 => (ushort)0x3A,                                   // levý Alt → Option
            0xA5 => (ushort)0x3D,                                   // pravý Alt / AltGr → pravý Option
            0x5B => swap ? (ushort)0x3B : (ushort)0x37,             // levý Win
            0x5C => (ushort)0x36,                                   // pravý Win → pravý Command
            0x14 => (ushort)0x39,                                   // Caps Lock
            _ => KeyMap.ToMac(scan, extended, swap),
        };
        if (code == null) return false;

        bool isModifier = vk is 0x10 or 0x14 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);
        if (!up && isModifier && _pressed.Contains(code.Value))
            return true; // Windows při držení opakuje stisk i u Shiftu – na Mac posíláme jen první

        if (up) _pressed.Remove(code.Value);
        else _pressed.Add(code.Value);
        _client.Post(MsgType.Key, Bin.Key(code.Value, !up));
        return true;
    }

    /// <summary>Pustí všechny držené klávesy, aby na Macu nezůstal „zaseknutý“ např. Cmd.</summary>
    private void ReleaseKeys()
    {
        _hostKeyDown = false;
        if (_pressed.Count == 0) return;
        foreach (var code in _pressed) _client.Post(MsgType.Key, Bin.Key(code, false));
        _pressed.Clear();
        _client.Post(MsgType.ReleaseAll);
    }

    // ---------------- celá obrazovka ----------------

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState;
            _prevBorder = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
            _fullscreen = true;
            _view.IsFullscreen = true;
            _view.ShowToast("Celá obrazovka – návrat: menu u šipky vpravo nebo pravý Ctrl + F", Theme.Accent);
        }
        else
        {
            FormBorderStyle = _prevBorder;
            WindowState = _prevState;
            _fullscreen = false;
            _view.IsFullscreen = false;
        }
        _view.Invalidate();
        _view.Focus();
        UpdateKeyboardLabel();
    }
}
