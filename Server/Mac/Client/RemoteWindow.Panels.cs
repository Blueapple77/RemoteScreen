using System.Runtime.InteropServices;
using System.Text;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Plovoucí panely vlevo nad obrazem (jako na Windows): Soubory, Terminál, Ovládání, Obraz.</summary>
internal sealed partial class RemoteWindow
{
    private enum PanelKind { None, Files, Terminal, Control, Settings }

    private static readonly (string Name, int Quality, int MaxWidth)[] Qualities =
    {
        ("Nízká – rychlá", 50, 1280),
        ("Střední", 70, 1920),
        ("Vysoká", 85, 2560),
        ("Plné rozlišení", 90, 0),
    };
    private static readonly int[] AllFps = { 15, 30, 60, 90, 120, 144 };

    private PanelKind _open = PanelKind.None;
    private IntPtr _panel, _panelBody, _panelTitle, _panelClose;
    private readonly Dictionary<PanelKind, IntPtr> _pages = new();
    private readonly Dictionary<PanelKind, Action<double, double>> _pageLayouts = new();

    // ---------------- rámeček panelu ----------------

    private void TogglePanel(PanelKind kind)
    {
        if (_open == kind) ClosePanel();
        else OpenPanel(kind);
    }

    private void OpenPanel(PanelKind kind)
    {
        if (_panel == IntPtr.Zero) BuildPanelFrame();
        if (!_pages.ContainsKey(kind))
        {
            IntPtr page = ObjC.NewWithFrame(ConnectWindow.FlippedViewClass(), new CGRect(0, 0, 400, 400));
            _pages[kind] = page;
            switch (kind)
            {
                case PanelKind.Files: BuildFilesPage(page); break;
                case PanelKind.Terminal: BuildTerminalPage(page); break;
                case PanelKind.Control: BuildControlPage(page); break;
                default: BuildSettingsPage(page); break;
            }
        }
        foreach (var p in _pages.Values) ObjC.SetBool(p, "setHidden:", true);
        IntPtr current = _pages[kind];
        if (ObjC.Get(current, "superview") != _panelBody) UI.Add(_panelBody, current);
        ObjC.SetBool(current, "setHidden:", false);
        UI.SetText(_panelTitle, kind switch
        {
            PanelKind.Files => "Soubory",
            PanelKind.Terminal => "Terminál",
            PanelKind.Control => "Ovládání",
            _ => "Obraz a klávesnice",
        });
        _open = kind;
        LayoutPanel();
        ObjC.SetBool(_panel, "setHidden:", false);
        RefreshDock();
        ReleaseKeys();

        if (kind == PanelKind.Files && _entries.Length == 0) RequestList(_remotePath.Length > 0 ? _remotePath : "~");
        if (kind == PanelKind.Terminal) ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _termInput);
        if (kind == PanelKind.Control) RunAction("volume_get");
    }

    private void ClosePanel()
    {
        if (_panel != IntPtr.Zero) ObjC.SetBool(_panel, "setHidden:", true);
        _open = PanelKind.None;
        RefreshDock();
        ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _view);
    }

    private void BuildPanelFrame()
    {
        _panel = UI.Glass(new CGRect(14, 14, 400, 400), 13 /* HUD */, behindWindow: false, cornerRadius: 18);
        UI.Rounded(_panel, 18, null, ObjC.Color(1, 1, 1, 0.08));
        IntPtr inner = ObjC.NewWithFrame(ConnectWindow.FlippedViewClass(), new CGRect(0, 0, 400, 400));
        UI.Autoresize(inner, 2 | 16);
        UI.Add(_panel, inner);
        _panelTitle = UI.Label("", 15, UI.WeightSemibold, UI.Text);
        UI.Frame(_panelTitle, 18, 14, 300, 22);
        UI.Add(inner, _panelTitle);
        _panelClose = UI.IconButton("xmark", "Zavřít panel", ClosePanel);
        _controls.Add(_panelClose);
        UI.Add(inner, _panelClose);
        _panelBody = ObjC.NewWithFrame(ConnectWindow.FlippedViewClass(), new CGRect(0, 48, 400, 352));
        UI.Add(inner, _panelBody);
        ObjC.SetBool(_panel, "setHidden:", true);
        UI.Add(_content, _panel);
        // dok, stav a oznámení zůstanou nad panelem
        foreach (var v in new[] { _dock, _handle, _pill, _toast }) UI.Add(_content, v);
    }

    /// <summary>Velikost panelu podle okna (vlevo, nad dokem) a rozložení aktuální stránky.</summary>
    private void LayoutPanel()
    {
        if (_panel == IntPtr.Zero || _open == PanelKind.None) return;
        var b = ObjC.Rect(_content, "bounds");
        double want = _open switch
        {
            PanelKind.Files => 540,
            PanelKind.Terminal => 580,
            PanelKind.Control => 420,
            _ => 360,
        };
        double w = Math.Max(300, Math.Min(want, b.W - 28 - (_dockOpen ? DockW + 30 : HandleW + 6)));
        double h = Math.Max(220, b.H - 28);
        UI.Frame(_panel, 14, 14, w, h);
        UI.Frame(_panelClose, w - 46, 10, 34, 30);
        UI.Frame(_panelBody, 0, 48, w, h - 48);
        IntPtr page = _pages[_open];
        UI.Frame(page, 0, 0, w, h - 48);
        if (_pageLayouts.TryGetValue(_open, out var layout)) layout(w, h - 48);
    }

    /// <summary>Svislý seznam prvků v pevné šířce (sekce, řádky tlačítek).</summary>
    private sealed class Stack
    {
        private readonly IntPtr _parent;
        private readonly double _x, _w;
        public double Y;

        public Stack(IntPtr parent, double x, double w, double y = 0) { _parent = parent; _x = x; _w = w; Y = y; }

        public void Section(string title)
        {
            Y += Y > 4 ? 14 : 2;
            IntPtr l = UI.Label(title.ToUpperInvariant(), 10.5, UI.WeightBold, UI.TextDim);
            UI.Frame(l, _x + 2, Y, _w, 14);
            UI.Add(_parent, l);
            Y += 20;
        }

        public void Row(params IntPtr[] controls) => RowH(32, controls);

        /// <summary>Řádek s danou výškou (pozor: IntPtr = nint, proto jiný název než Row).</summary>
        public void RowH(double h, params IntPtr[] controls)
        {
            double gap = 8, cw = (_w - gap * (controls.Length - 1)) / controls.Length;
            for (int i = 0; i < controls.Length; i++)
            {
                UI.Frame(controls[i], _x + i * (cw + gap), Y, cw, h);
                UI.Add(_parent, controls[i]);
            }
            Y += h + 8;
        }

        /// <summary>Prvky s pevnými šířkami (poslední vyplní zbytek, pokud má šířku 0).</summary>
        public void RowWidths(double h, params (IntPtr Control, double Width)[] items)
        {
            double fixedW = items.Where(i => i.Width > 0).Sum(i => i.Width) + 8 * (items.Length - 1);
            double x = _x;
            foreach (var (c, w0) in items)
            {
                double w = w0 > 0 ? w0 : Math.Max(40, _w - fixedW);
                UI.Frame(c, x, Y, w, h);
                UI.Add(_parent, c);
                x += w + 8;
            }
            Y += h + 8;
        }
    }

    private IntPtr PanelButton(string title, Action a)
    {
        IntPtr b = UI.Button(title, a);
        ObjC.SetInt(b, "setControlSize:", 0); // běžná velikost
        _controls.Add(b);
        return b;
    }

    /// <summary>Stránka s posuvníkem (pevný obsah, panel může být nižší).</summary>
    private (IntPtr Scroll, IntPtr Doc) ScrollPage(IntPtr page, PanelKind kind, double contentHeight)
    {
        IntPtr scroll = ObjC.NewWithFrame("NSScrollView", new CGRect(0, 0, 400, 300));
        ObjC.SetBool(scroll, "setDrawsBackground:", false);
        ObjC.SetBool(scroll, "setHasVerticalScroller:", true);
        ObjC.SetBool(scroll, "setAutohidesScrollers:", true);
        IntPtr doc = ObjC.NewWithFrame(ConnectWindow.FlippedViewClass(), new CGRect(0, 0, 400, contentHeight));
        ObjC.Set(scroll, "setDocumentView:", doc);
        ObjC.SetBool(ObjC.Get(scroll, "contentView"), "setDrawsBackground:", false);
        UI.Add(page, scroll);
        _pageLayouts[kind] = (w, h) =>
        {
            UI.Frame(scroll, 0, 0, w, h);
            var f = ObjC.Rect(doc, "frame");
            ObjC.SendSize(doc, ObjC.Sel("setFrameSize:"), new CGSize(w - 4, f.H));
        };
        return (scroll, doc);
    }

    // ================= Obraz a klávesnice =================

    private IntPtr _btnStream, _keysCheck;

    private void BuildSettingsPage(IntPtr page)
    {
        const double cw = 360 - 36;
        var (_, doc) = ScrollPage(page, PanelKind.Settings, 470);
        var st = new Stack(doc, 18, cw - 4, 4);

        _btnStream = PanelButton(_streaming ? "Zastavit obraz" : "Spustit obraz", () =>
        {
            if (_streaming) StopStream(); else StartStream();
            ObjC.Set(_btnStream, "setTitle:", ObjC.Str(_streaming ? "Zastavit obraz" : "Spustit obraz"));
        });
        st.RowH(32, _btnStream);

        if (_displays.Length > 0)
        {
            st.Section("Monitor");
            IntPtr disp = _displayPopup = UI.Popup(_displays.Select(d => $"{d.Index + 1}: {d.Width:0}×{d.Height:0}{(d.IsMain ? " (hlavní)" : "")}"), _displayIndex, () => { });
            UI.OnClick(disp, () => SelectDisplay(UI.SelectedIndex(disp)));
            _controls.Add(disp);
            st.RowH(28, disp);
        }

        st.Section("Kvalita obrazu");
        int qi = Array.FindIndex(Qualities, q => q.Quality == _settings.Quality && q.MaxWidth == _settings.MaxWidth);
        IntPtr quality = UI.Popup(Qualities.Select(q => q.Name), qi < 0 ? 2 : qi, () => { });
        UI.OnClick(quality, () =>
        {
            var q = Qualities[Math.Max(0, UI.SelectedIndex(quality))];
            _settings.Quality = q.Quality;
            _settings.MaxWidth = q.MaxWidth;
            _settings.Save();
            if (_streaming) StartStream();
        });
        _controls.Add(quality);
        st.RowH(28, quality);

        st.Section("Snímková frekvence");
        int fi = Array.IndexOf(AllFps, _settings.Fps);
        IntPtr fps = UI.Popup(AllFps.Select(f => $"{f} fps"), fi < 0 ? 2 : fi, () => { });
        UI.OnClick(fps, () =>
        {
            _settings.Fps = AllFps[Math.Max(0, UI.SelectedIndex(fps))];
            _settings.Save();
            if (_streaming) StartStream();
            if (_settings.Fps > 60) Toast($"Až {_settings.Fps} fps – skutečná rychlost závisí na síti, výkonu a monitoru druhého počítače.");
        });
        _controls.Add(fps);
        st.RowH(28, fps);

        st.Section("Klávesnice");
        _keysCheck = UI.Checkbox("Posílat klávesy na druhý počítač", _sendKeys, () =>
        {
            if (UI.IsChecked(_keysCheck) != _sendKeys) ToggleKeys();
        });
        _controls.Add(_keysCheck);
        st.RowH(22, _keysCheck);
        if (_targetWindows)
        {
            IntPtr swap = UI.Checkbox("Cmd funguje jako Ctrl (Cmd+C, Cmd+V…)", _settings.SwapCmdCtrl, () => { });
            UI.OnClick(swap, () => { if (UI.IsChecked(swap) != _settings.SwapCmdCtrl) ToggleSwap(); });
            _controls.Add(swap);
            st.RowH(22, swap);
        }

        st.Section("Vzhled – barva zvýraznění");
        st.RowH(34, AccentRow(cw - 4));

        st.Section("Soubory");
        IntPtr hint = UI.Label("Soubory stačí přetáhnout z Finderu přímo do obrazu – a opačně je v obrazu chytit a vytáhnout ven z okna.",
            11.5, UI.WeightRegular, UI.TextDim, wrap: true);
        st.RowH(48, hint);
        ObjC.SendSize(doc, ObjC.Sel("setFrameSize:"), new CGSize(cw, st.Y + 10));
    }

    private readonly List<(IntPtr Button, string Hex)> _swatches = new();

    /// <summary>Kolečka s barvami; vybraná má bílý kroužek. Projeví se hned a uloží se.</summary>
    private IntPtr AccentRow(double width)
    {
        IntPtr row = ObjC.NewWithFrame(ConnectWindow.FlippedViewClass(), new CGRect(0, 0, width, 34));
        double x = 2;
        foreach (var (name, hex) in UI.AccentPresets)
        {
            string h = hex;
            IntPtr b = ObjC.NewWithFrame("NSButton", new CGRect(x, 2, 30, 30));
            ObjC.SetBool(b, "setBordered:", false);
            ObjC.Set(b, "setTitle:", ObjC.Str(""));
            ObjC.Set(b, "setToolTip:", ObjC.Str(name));
            UI.OnClick(b, () => ChooseAccent(h));
            _controls.Add(b);
            UI.Add(row, b);
            _swatches.Add((b, h));
            x += 40;
        }
        PaintSwatches();
        return row;
    }

    private void PaintSwatches()
    {
        string current = UI.AccentHex;
        foreach (var (b, hex) in _swatches)
        {
            UI.SetAccent(hex);
            IntPtr color = UI.Accent;
            UI.SetAccent(current);
            bool sel = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            UI.Rounded(b, 15, color, sel ? ObjC.Color(1, 1, 1, 0.9) : null);
            if (sel) ObjC.SetDouble(ObjC.Get(b, "layer"), "setBorderWidth:", 2.5);
        }
    }

    private void ChooseAccent(string hex)
    {
        UI.SetAccent(hex);
        _settings.Accent = UI.AccentHex;
        _settings.Save();
        PaintSwatches();
        RefreshDock();
        if (_termCwd != IntPtr.Zero) ObjC.Set(_termCwd, "setTextColor:", UI.AccentText);
        Toast("Barva zvýraznění změněna.");
    }

    private IntPtr _displayPopup;

    private void SelectDisplay(int index)
    {
        if (index < 0 || index >= _displays.Length) return;
        _displayIndex = index;
        var (rw, rh) = RemoteSize();
        ObjC.SendSize(_window, ObjC.Sel("setContentAspectRatio:"), new CGSize(rw, rh));
        StartStream();
        UpdateMonitorItem();
        if (_displayPopup != IntPtr.Zero) // výběr v panelu Obraz ať odpovídá menu
            ObjC.SendInt(_displayPopup, ObjC.Sel("selectItemAtIndex:"), index);
    }

    private void StopStream()
    {
        _client.Post(MsgType.StreamStop);
        _streaming = false;
        UI.SetText(_placeholder, "Obraz je zastavený – spustíte ho v panelu Obraz.");
        ObjC.SetBool(_placeholder, "setHidden:", false);
        UpdatePill();
    }

    // ================= Ovládání =================

    private IntPtr _volume, _appField, _urlField;

    private void BuildControlPage(IntPtr page)
    {
        const double cw = 420 - 36;
        var (_, doc) = ScrollPage(page, PanelKind.Control, 760);
        var st = new Stack(doc, 18, cw - 4, 4);

        st.Section("Zvuk");
        _volume = ObjC.NewWithFrame("NSSlider", new CGRect(0, 0, 200, 24));
        ObjC.SetDouble(_volume, "setMinValue:", 0);
        ObjC.SetDouble(_volume, "setMaxValue:", 100);
        ObjC.SetDouble(_volume, "setDoubleValue:", 50);
        ObjC.SetBool(_volume, "setContinuous:", false);
        UI.OnClick(_volume, () => RunAction("volume_set", ((int)ObjC.SendRetDouble(_volume, ObjC.Sel("doubleValue"))).ToString()));
        _controls.Add(_volume);
        st.RowH(24, _volume);
        st.Row(PanelButton("Tišeji", () => RunAction("volume_down")), PanelButton("Hlasitěji", () => RunAction("volume_up")),
            PanelButton("Ztlumit", () => RunAction("volume_mute")));

        st.Section("Hudba");
        st.Row(PanelButton("⏮", () => RunAction("media_prev")), PanelButton("Přehrát / pauza", () => RunAction("media_playpause")),
            PanelButton("⏭", () => RunAction("media_next")));

        st.Section("Schránka");
        IntPtr clipSync = UI.Checkbox("Sdílet schránku automaticky (text, oběma směry)", _settings.ClipboardSync, () => { });
        UI.OnClick(clipSync, () =>
        {
            _settings.ClipboardSync = UI.IsChecked(clipSync);
            _settings.Save();
            if (_settings.ClipboardSync) StartClipSync(); else StopClipSync();
        });
        _controls.Add(clipSync);
        st.RowH(22, clipSync);
        st.Row(PanelButton("Napsat text ze schránky", TypeClipboard));
        IntPtr typeHint = UI.Label("Vepíše text ze schránky tohoto Macu do aktivního okna – i když se liší rozložení klávesnice.",
            11.5, UI.WeightRegular, UI.TextDim, wrap: true);
        st.RowH(32, typeHint);

        st.Section("Aplikace");
        _appField = UI.TextField(_targetWindows ? "Název nebo cesta (např. notepad)" : "Název aplikace (např. Safari)", secure: false);
        ObjC.Set(_appField, "setFont:", ObjC.Font(13));
        st.RowH(26, _appField);
        st.Row(PanelButton("Spustit / přepnout", () => { string a = UI.GetText(_appField).Trim(); if (a.Length > 0) RunAction("open_app", a); }),
            PanelButton("Ukončit", () => { string a = UI.GetText(_appField).Trim(); if (a.Length > 0) RunAction("quit_app", a); }));

        st.Section("Otevřít adresu nebo soubor");
        _urlField = UI.TextField("https://…", secure: false);
        ObjC.Set(_urlField, "setFont:", ObjC.Font(13));
        st.RowWidths(26, (_urlField, 0), (PanelButton("Otevřít", () => { string u = UI.GetText(_urlField).Trim(); if (u.Length > 0) RunAction("open", u); }), 90));

        string target = _targetWindows ? "PC" : "Mac";
        st.Section("Systém");
        st.Row(PanelButton("Rozsvítit displej", () => RunAction("display_wake")), PanelButton("Vypnout displej", () => RunAction("display_sleep")));
        st.Row(PanelButton("Zamknout", () => RunAction("lock")), PanelButton("Uspat", () => RunAction("sleep")));
        st.Row(PanelButton(_targetWindows ? "Správce úloh" : "Vynutit ukončení…", () => RunAction("force_quit")),
            PanelButton(_targetWindows ? "Přepnout okna" : "Mission Control", () => RunAction("mission_control")));
        st.Row(PanelButton("Odhlásit…", () => Confirmed($"Odhlásit uživatele na {target}?", "logout")),
            PanelButton("Restartovat…", () => Confirmed($"Restartovat {target}? Spojení se přeruší.", "restart")),
            PanelButton("Vypnout…", () => Confirmed($"Vypnout {target}? Na dálku ho znovu nezapnete.", "shutdown")));

        ObjC.SendSize(doc, ObjC.Sel("setFrameSize:"), new CGSize(cw, st.Y + 10));
    }

    private void Confirmed(string question, string action)
    {
        ReleaseKeys();
        if (UI.Alert(question, "", new[] { "Zrušit", "Pokračovat" }, warning: true) == 1) RunAction(action);
    }

    /// <summary>Hlasitost z odpovědi serveru → posuvník.</summary>
    private void OnVolume(ActionResult r)
    {
        if (_volume == IntPtr.Zero || !r.Ok || !int.TryParse(r.Output, out var v)) return;
        ObjC.SetDouble(_volume, "setDoubleValue:", Math.Clamp(v, 0, 100));
    }

    // ================= Terminál =================

    private IntPtr _termScroll, _termText, _termInput, _termCwd, _termRun, _termStop, _termClear;
    private int _execId = 1000, _runningExec = -1;
    private string _termCwdText = "";
    private readonly List<(string Text, int Kind)> _termQueue = new(); // 0 text, 1 chyba, 2 příkaz
    private int _termFlushQueued;

    private void BuildTerminalPage(IntPtr page)
    {
        _termScroll = ObjC.Get(ObjC.Cls("NSTextView"), "scrollableTextView");
        _termText = ObjC.Get(_termScroll, "documentView");
        ObjC.SetBool(_termText, "setEditable:", false);
        ObjC.SetBool(_termText, "setRichText:", true);
        ObjC.Set(_termText, "setBackgroundColor:", ObjC.Color(0.03, 0.03, 0.04, 0.45)); // průsvitné – sklo panelu prosvítá
        ObjC.SetBool(_termScroll, "setDrawsBackground:", false);
        ObjC.Set(_termText, "setTextColor:", UI.Text);
        ObjC.Set(_termText, "setFont:", MonoFont());
        ObjC.SendSize(_termText, ObjC.Sel("setTextContainerInset:"), new CGSize(8, 8));
        UI.Rounded(_termScroll, 10);
        UI.Add(page, _termScroll);

        _termCwd = UI.Label(_targetWindows ? "C:\\>" : "~ %", 11.5, UI.WeightMedium, UI.AccentText);
        ObjC.SetInt(_termCwd, "setLineBreakMode:", 3); // zkrátit začátek
        UI.Add(page, _termCwd);
        _termInput = UI.TextField(_targetWindows ? "Příkaz (např. dir) a Enter" : "Příkaz (např. ls -la) a Enter", secure: false);
        ObjC.Set(_termInput, "setFont:", MonoFont());
        UI.OnClick(_termInput, RunCommand); // Enter
        _controls.Add(_termInput);
        UI.Add(page, _termInput);
        _termRun = PanelButton("Spustit", RunCommand);
        _termStop = PanelButton("Zastavit", () => { if (_runningExec >= 0) _client.PostJson(MsgType.ExecKill, new ExecKill(_runningExec)); });
        _termClear = PanelButton("Vymazat", () => ObjC.Set(_termText, "setString:", ObjC.Str("")));
        UI.Add(page, _termRun);
        UI.Add(page, _termStop);
        UI.Add(page, _termClear);
        ObjC.SetBool(_termStop, "setEnabled:", false);

        _pageLayouts[PanelKind.Terminal] = (w, h) =>
        {
            double x = 14, iw = w - 28;
            UI.Frame(_termScroll, x, 0, iw, Math.Max(60, h - 98));
            UI.Frame(_termCwd, x, h - 90, iw, 16);
            UI.Frame(_termInput, x, h - 70, iw, 26);
            double bw = (iw - 16) / 3;
            UI.Frame(_termRun, x, h - 38, bw, 28);
            UI.Frame(_termStop, x + bw + 8, h - 38, bw, 28);
            UI.Frame(_termClear, x + 2 * (bw + 8), h - 38, bw, 28);
        };
        AppendTerm(_targetWindows
            ? "Příkazy běží v příkazovém řádku Windows (cmd) na druhém počítači.\n"
            : "Příkazy běží v zsh na druhém Macu.\n", 1);
    }

    private static IntPtr MonoFont()
    {
        IntPtr f = ObjC.RespondsTo(ObjC.Cls("NSFont"), "monospacedSystemFontOfSize:weight:")
            ? ObjC.SendDouble2(ObjC.Cls("NSFont"), ObjC.Sel("monospacedSystemFontOfSize:weight:"), 12, 0)
            : ObjC.SendDouble(ObjC.Cls("NSFont"), ObjC.Sel("userFixedPitchFontOfSize:"), 12);
        return f;
    }

    private void RunCommand()
    {
        string cmd = UI.GetText(_termInput).Trim();
        if (cmd.Length == 0) return;
        if (_runningExec >= 0)
        {
            Toast("Předchozí příkaz ještě běží – počkejte nebo ho zastavte.");
            return;
        }
        _runningExec = Interlocked.Increment(ref _execId);
        ObjC.Set(_termInput, "setStringValue:", ObjC.Str(""));
        ObjC.SetBool(_termStop, "setEnabled:", true);
        AppendTerm($"{(_termCwdText.Length > 0 ? _termCwdText : "")} $ {cmd}\n", 2);
        _client.PostJson(MsgType.Exec, new ExecRequest(_runningExec, cmd));
    }

    /// <summary>Výstup příkazu (z libovolného vlákna) – zobrazí se dávkově na hlavním vlákně.</summary>
    private void AppendTerm(string text, int kind)
    {
        lock (_termQueue) _termQueue.Add((text, kind));
        if (Interlocked.Exchange(ref _termFlushQueued, 1) == 0)
            MenuBarApp.RunOnMain(FlushTerminal);
    }

    private void FlushTerminal()
    {
        Volatile.Write(ref _termFlushQueued, 0);
        if (_closed || _termText == IntPtr.Zero) return;
        List<(string Text, int Kind)> items;
        lock (_termQueue)
        {
            if (_termQueue.Count == 0) return;
            items = _termQueue.ToList();
            _termQueue.Clear();
        }
        IntPtr storage = ObjC.Get(_termText, "textStorage");
        foreach (var (text, kind) in items)
        {
            IntPtr attrs = ObjC.Get(ObjC.Cls("NSMutableDictionary"), "dictionary");
            ObjC.Send(attrs, ObjC.Sel("setObject:forKey:"), MonoFont(), ObjC.Str("NSFont"));
            ObjC.Send(attrs, ObjC.Sel("setObject:forKey:"), kind switch { 1 => UI.TextDim, 2 => UI.AccentText, 3 => UI.Danger, _ => UI.Text }, ObjC.Str("NSColor"));
            IntPtr attr = ObjC.Send(ObjC.Send(ObjC.Cls("NSAttributedString"), ObjC.Sel("alloc")), ObjC.Sel("initWithString:attributes:"), ObjC.Str(text), attrs);
            ObjC.Send(storage, ObjC.Sel("appendAttributedString:"), attr);
            ObjC.Send(attr, ObjC.Sel("release"));
        }
        ObjC.Send(_termText, ObjC.Sel("scrollToEndOfDocument:"), IntPtr.Zero);
    }

    private void OnExecExit(ExecExit e)
    {
        if (e.Id != _runningExec) return;
        _runningExec = -1;
        if (_termStop != IntPtr.Zero) ObjC.SetBool(_termStop, "setEnabled:", false);
        if (e.Cwd.Length > 0)
        {
            _termCwdText = e.Cwd;
            if (_termCwd != IntPtr.Zero) UI.SetText(_termCwd, e.Cwd);
        }
        if (e.ExitCode != 0) AppendTerm($"(kód {e.ExitCode})\n", 1);
    }

    // ================= Soubory =================

    private IntPtr _pathField, _filesTable, _filesScroll, _filesStatus;
    private IntPtr _fUp, _fHome, _fRefresh, _fDownload, _fUpload, _fMkdir, _fDelete, _fOpen;
    private DirEntry[] _entries = Array.Empty<DirEntry>();
    private string _remotePath = "";
    private static readonly Dictionary<IntPtr, RemoteWindow> ByTable = new();
    private static IntPtr _sourceClass;

    private void BuildFilesPage(IntPtr page)
    {
        _pathField = UI.TextField("Cesta", secure: false);
        ObjC.Set(_pathField, "setFont:", ObjC.Font(12.5));
        UI.OnClick(_pathField, () => RequestList(UI.GetText(_pathField).Trim()));
        _controls.Add(_pathField);
        UI.Add(page, _pathField);
        _fUp = UI.IconButton("arrow.up", "O úroveň výš", () => RequestList(ParentPath(_remotePath)));
        _fHome = UI.IconButton("house", "Domovská složka", () => RequestList("~"));
        _fRefresh = UI.IconButton("arrow.clockwise", "Obnovit", () => RequestList(_remotePath));
        foreach (var b in new[] { _fUp, _fHome, _fRefresh }) { _controls.Add(b); UI.Add(page, b); }

        _filesScroll = ObjC.NewWithFrame("NSScrollView", new CGRect(0, 0, 400, 300));
        ObjC.SetBool(_filesScroll, "setHasVerticalScroller:", true);
        ObjC.SetBool(_filesScroll, "setAutohidesScrollers:", true);
        ObjC.SetBool(_filesScroll, "setDrawsBackground:", false);
        _filesTable = ObjC.NewWithFrame("NSTableView", new CGRect(0, 0, 400, 300));
        AddColumn(_filesTable, "name", "Název", 300);
        AddColumn(_filesTable, "size", "Velikost", 90);
        ObjC.SetDouble(_filesTable, "setRowHeight:", 22);
        ObjC.Set(_filesTable, "setBackgroundColor:", UI.Clear);
        ObjC.SetBool(_filesTable, "setAllowsMultipleSelection:", false);
        ObjC.SetInt(_filesTable, "setColumnAutoresizingStyle:", 5); // první sloupec se přizpůsobí
        ByTable[_filesTable] = this;
        ObjC.Set(_filesTable, "setDataSource:", TableSource());
        UI.OnDoubleClick(_filesTable, OpenSelected);
        _controls.Add(_filesTable);
        ObjC.Set(_filesScroll, "setDocumentView:", _filesTable);
        UI.Rounded(_filesScroll, 10);
        UI.Add(page, _filesScroll);

        _filesStatus = UI.Label("", 11.5, UI.WeightRegular, UI.TextDim);
        ObjC.SetInt(_filesStatus, "setLineBreakMode:", 4);
        UI.Add(page, _filesStatus);

        _fDownload = PanelButton("Stáhnout", DownloadSelected);
        _fUpload = PanelButton("Nahrát…", UploadToCurrent);
        _fMkdir = PanelButton("Nová složka", MakeDir);
        _fDelete = PanelButton("Smazat", DeleteSelected);
        _fOpen = PanelButton("Otevřít tam", () =>
        {
            var e = SelectedEntry();
            if (e != null) RunAction("open", _files.Combine(_remotePath, e.Name));
        });
        foreach (var b in new[] { _fDownload, _fUpload, _fMkdir, _fDelete, _fOpen }) UI.Add(page, b);

        _pageLayouts[PanelKind.Files] = (w, h) =>
        {
            double x = 14, iw = w - 28;
            UI.Frame(_pathField, x, 0, iw - 3 * 34, 26);
            UI.Frame(_fUp, x + iw - 3 * 34 + 2, -2, 32, 30);
            UI.Frame(_fHome, x + iw - 2 * 34 + 2, -2, 32, 30);
            UI.Frame(_fRefresh, x + iw - 34 + 2, -2, 32, 30);
            UI.Frame(_filesScroll, x, 34, iw, Math.Max(60, h - 34 - 114));
            UI.Frame(_filesStatus, x, h - 104, iw, 16);
            double bw3 = (iw - 16) / 3, bw2 = (iw - 8) / 2;
            UI.Frame(_fDownload, x, h - 80, bw3, 28);
            UI.Frame(_fUpload, x + bw3 + 8, h - 80, bw3, 28);
            UI.Frame(_fMkdir, x + 2 * (bw3 + 8), h - 80, bw3, 28);
            UI.Frame(_fDelete, x, h - 44, bw2, 28);
            UI.Frame(_fOpen, x + bw2 + 8, h - 44, bw2, 28);
        };
    }

    private static void AddColumn(IntPtr table, string id, string title, double width)
    {
        IntPtr col = ObjC.Send(ObjC.Send(ObjC.Cls("NSTableColumn"), ObjC.Sel("alloc")), ObjC.Sel("initWithIdentifier:"), ObjC.Str(id));
        ObjC.Set(col, "setTitle:", ObjC.Str(title));
        ObjC.SetDouble(col, "setWidth:", width);
        ObjC.Send(table, ObjC.Sel("addTableColumn:"), col);
        ObjC.Send(col, ObjC.Sel("release"));
    }

    private static unsafe IntPtr TableSource()
    {
        if (_sourceClass == IntPtr.Zero)
        {
            IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSObject"), "RSFilesSource", 0);
            ObjC.class_addMethod(cls, ObjC.Sel("numberOfRowsInTableView:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint>)&OnRowCount, "q@:@");
            ObjC.class_addMethod(cls, ObjC.Sel("tableView:objectValueForTableColumn:row:"),
                (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, nint, IntPtr>)&OnCellValue, "@@:@@q");
            ObjC.objc_registerClassPair(cls);
            _sourceClass = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
        }
        return _sourceClass;
    }

    [UnmanagedCallersOnly]
    private static nint OnRowCount(IntPtr self, IntPtr sel, IntPtr table) =>
        ByTable.TryGetValue(table, out var w) ? w._entries.Length : 0;

    [UnmanagedCallersOnly]
    private static IntPtr OnCellValue(IntPtr self, IntPtr sel, IntPtr table, IntPtr column, nint row)
    {
        try
        {
            if (!ByTable.TryGetValue(table, out var w) || row < 0 || row >= w._entries.Length) return IntPtr.Zero;
            var e = w._entries[row];
            string id = ObjC.ToStr(ObjC.Get(column, "identifier")) ?? "";
            return ObjC.Str(id == "size"
                ? (e.IsDir ? "—" : FileTransfers.FormatSize(e.Size))
                : (e.IsDir ? "📁  " : "📄  ") + e.Name);
        }
        catch { return IntPtr.Zero; }
    }

    private DirEntry? SelectedEntry()
    {
        nint row = ObjC.SendRetInt(_filesTable, ObjC.Sel("clickedRow"));
        if (row < 0 || row >= _entries.Length) row = ObjC.SendRetInt(_filesTable, ObjC.Sel("selectedRow"));
        return row >= 0 && row < _entries.Length ? _entries[row] : null;
    }

    private void RequestList(string path)
    {
        if (path.Length == 0) path = "~";
        _client.PostJson(MsgType.ListDir, new PathRequest(path));
        if (_filesStatus != IntPtr.Zero) UI.SetText(_filesStatus, "Načítám…");
    }

    private void OnDirListing(DirListing l)
    {
        if (_filesTable == IntPtr.Zero) return;
        if (!l.Ok)
        {
            UI.SetText(_filesStatus, l.Error ?? "Složku nelze otevřít.");
            ObjC.Set(_filesStatus, "setTextColor:", UI.Danger);
            return;
        }
        _remotePath = l.Path;
        _entries = l.Entries.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ObjC.Set(_pathField, "setStringValue:", ObjC.Str(l.Path));
        ObjC.Send(_filesTable, ObjC.Sel("reloadData"));
        ObjC.Set(_filesStatus, "setTextColor:", UI.TextDim);
        int dirs = _entries.Count(e => e.IsDir);
        UI.SetText(_filesStatus, $"{dirs} složek · {_entries.Length - dirs} souborů");
    }

    private string ParentPath(string path)
    {
        string p = path.TrimEnd('/', '\\');
        int i = Math.Max(p.LastIndexOf('/'), p.LastIndexOf('\\'));
        if (i < 0) return _targetWindows ? p : "/";
        string parent = p[..i];
        if (parent.Length == 0) return "/";
        if (_targetWindows && parent.Length == 2 && parent[1] == ':') return parent + "\\";
        return parent;
    }

    private void OpenSelected()
    {
        var e = SelectedEntry();
        if (e == null) return;
        if (e.IsDir) RequestList(_files.Combine(_remotePath, e.Name));
        else DownloadSelected();
    }

    private void DownloadSelected()
    {
        var e = SelectedEntry();
        if (e == null) { Toast("Vyberte soubor."); return; }
        if (e.IsDir) { Toast("Složku stáhnete tak, že ji v obrazu chytíte a vytáhnete ven z okna."); return; }
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        string name = e.Name;
        string dest = Path.Combine(downloads, name);
        for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
            dest = Path.Combine(downloads, $"{Path.GetFileNameWithoutExtension(e.Name)} {i}{Path.GetExtension(e.Name)}");
        string relative = Path.GetFileName(dest);
        var item = new DragItem(_files.Combine(_remotePath, e.Name), relative, e.Size);
        _ = Task.Run(async () =>
        {
            Toast($"Stahuji {e.Name}…", sticky: true);
            string? err = await _files.DownloadAsync(new[] { item }, downloads, (d, t) =>
                Toast($"Stahuji {e.Name} – {(t > 0 ? d * 100 / t : 100)} %", sticky: true));
            if (err != null) { Toast("Stažení selhalo: " + err, error: true); return; }
            MenuBarApp.RunOnMain(() =>
            {
                Toast($"Staženo do Stažených souborů: {relative}");
                IntPtr url = ObjC.Send(ObjC.Cls("NSURL"), ObjC.Sel("fileURLWithPath:"), ObjC.Str(dest));
                ObjC.Send(ObjC.Get(ObjC.Cls("NSWorkspace"), "sharedWorkspace"), ObjC.Sel("activateFileViewerSelectingURLs:"),
                    ObjC.Send(ObjC.Cls("NSArray"), ObjC.Sel("arrayWithObject:"), url));
            });
        });
    }

    private void UploadToCurrent()
    {
        if (_remotePath.Length == 0) return;
        IntPtr panel = ObjC.Get(ObjC.Cls("NSOpenPanel"), "openPanel");
        ObjC.SetBool(panel, "setCanChooseFiles:", true);
        ObjC.SetBool(panel, "setCanChooseDirectories:", true);
        ObjC.SetBool(panel, "setAllowsMultipleSelection:", true);
        ObjC.Set(panel, "setPrompt:", ObjC.Str("Nahrát"));
        ObjC.Set(panel, "setMessage:", ObjC.Str("Nahrát do: " + _remotePath));
        ReleaseKeys();
        if (ObjC.SendRetInt(panel, ObjC.Sel("runModal")) != 1) return;
        IntPtr urls = ObjC.Get(panel, "URLs");
        var paths = new List<string>();
        nuint n = ObjC.SendRetUInt(urls, ObjC.Sel("count"));
        for (nuint i = 0; i < n; i++)
        {
            string? p = ObjC.ToStr(ObjC.Get(ObjC.SendRetObjInt(urls, ObjC.Sel("objectAtIndex:"), i), "path"));
            if (p != null) paths.Add(p);
        }
        string dir = _remotePath;
        _ = Task.Run(async () =>
        {
            var r = await _files.UploadAsync(paths, dir, overwrite: false, (text, _) => Toast(text, sticky: true));
            Toast(r.Error ?? $"Nahráno {r.Files} soubor(ů) do {dir}", error: r.Error != null);
            MenuBarApp.RunOnMain(() => { if (_remotePath == dir) RequestList(dir); });
        });
    }

    private void MakeDir()
    {
        if (_remotePath.Length == 0) return;
        ReleaseKeys();
        string? name = UI.Prompt("Nová složka", "Název nové složky v " + _remotePath + ":", "", "Nová složka");
        if (string.IsNullOrWhiteSpace(name)) return;
        _client.PostJson(MsgType.MakeDir, new PathRequest(_files.Combine(_remotePath, name.Trim())));
    }

    private void DeleteSelected()
    {
        var e = SelectedEntry();
        if (e == null) { Toast("Vyberte soubor nebo složku."); return; }
        ReleaseKeys();
        if (UI.Alert($"Přesunout „{e.Name}“ do koše?", "Položka se na druhém počítači přesune do koše.",
                new[] { "Zrušit", "Do koše" }, warning: true) != 1) return;
        _client.PostJson(MsgType.Delete, new PathRequest(_files.Combine(_remotePath, e.Name)));
    }

    /// <summary>Po vytvoření/smazání/nahrání obnovit seznam.</summary>
    private void OnFileOpDone(FileOpResult r)
    {
        if (!r.Ok || _open != PanelKind.Files || _remotePath.Length == 0) return;
        if (r.Op is "mkdir" or "delete") RequestList(_remotePath);
    }
}
