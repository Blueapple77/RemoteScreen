using RemoteScreen.Client.UI;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

public sealed partial class MainForm
{
    private readonly Slider _volume = new() { Width = 230 };
    private readonly Label _lblVolume = new() { AutoSize = true, Text = "–", Margin = new Padding(6, 8, 0, 0), BackColor = Color.Transparent };
    private readonly ComboBox _appName = new ComboBox { Width = 230 }.Styled();
    private readonly ListBox _apps = new() { Width = 320, Height = 150, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly TextBox _clipText = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 320, Height = 110, AcceptsReturn = true, BorderStyle = BorderStyle.None };
    private readonly InputBox _urlText = new() { Width = 230, Text = "https://" };
    private readonly InputBox _notifyText = new() { Width = 230, Placeholder = "Text oznámení nebo k přečtení" };
    private readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
    };

    private void BuildControlPage()
    {
        foreach (var c in new Control[] { _apps, _clipText, _log })
        {
            c.BackColor = Theme.Input;
            c.ForeColor = Theme.Text;
            c.Font = Theme.Body;
        }
        _log.Font = new Font(Theme.Mono.FontFamily, 9f);
        _lblVolume.ForeColor = Theme.TextDim;
        _lblVolume.Font = Theme.Body;

        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            Padding = new Padding(4, 4, 4, 12), BackColor = Color.Transparent,
        };
        // jen svislé posouvání – karty se vždy přizpůsobí šířce panelu (viz LayoutCards)
        flow.HorizontalScroll.Maximum = 0;
        flow.HorizontalScroll.Visible = false;
        flow.AutoScroll = true;

        // karty a prvky, které se mají roztáhnout na celou šířku karty (s odečtem pevné části řádku)
        var cards = new List<Card>();
        var stretch = new Dictionary<Card, List<(Control C, int Reserved)>>();
        Card? building = null;
        T Stretch<T>(T c, int reserved = 0) where T : Control
        {
            if (building != null) stretch[building].Add((c, reserved));
            return c;
        }

        FlatButton B(string text, string glyph, Action onClick, ButtonKind kind = ButtonKind.Secondary)
        {
            var b = new FlatButton { Text = text, Glyph = glyph, Kind = kind };
            b.Click += (_, _) => onClick();
            return b;
        }
        void A(string name, string? arg = null) => _client.PostJson(MsgType.Action, new ActionRequest(name, arg));
        void Confirmed(string question, string action)
        {
            if (MessageBox.Show(this, question, "Potvrzení", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                A(action);
        }
        // karta se začne „stavět“ před vytvořením jejích řádků, aby si Stretch() pamatoval, kam patří
        Card Begin(string title, string glyph)
        {
            building = new Card { Text = title, Glyph = glyph, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(8, 6, 8, 6) };
            stretch[building] = new();
            return building;
        }
        Card MakeCard(Card card, params Control[] rows)
        {
            // průhledné vnitřní panely – jinak uvnitř průsvitné karty vzniknou tmavé obdélníky
            var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
            inner.Controls.AddRange(rows);
            inner.Location = new Point(card.Padding.Left, card.Padding.Top);
            card.Controls.Add(inner);
            cards.Add(card);
            building = null;
            return card;
        }
        FlowLayoutPanel Row(params Control[] items)
        {
            var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2), BackColor = Color.Transparent };
            r.Controls.AddRange(items);
            return r;
        }
        Panel Framed(Control c)
        {
            // standardní textová pole v zaobleném rámečku
            var host = new Card { Padding = new Padding(8), Margin = new Padding(3), BackColor = Theme.Input, Width = c.Width + 18, Height = c.Height + 18 };
            c.Dock = DockStyle.Fill;
            host.Controls.Add(c);
            return host;
        }

        // --- zvuk ---
        _volume.ValueChanged += (_, _) => _lblVolume.Text = _volume.Value + " %";
        _volume.ValueCommitted += (_, _) => A("volume_set", _volume.Value.ToString());
        var cSound = Begin("Zvuk a hudba", Glyph.Volume);
        flow.Controls.Add(MakeCard(cSound,
            Row(Stretch(_volume, 6 + 50), _lblVolume),
            Row(B("Tišeji", Glyph.VolumeLow, () => A("volume_down")), B("Hlasitěji", Glyph.Volume, () => A("volume_up")), B("Ztlumit", Glyph.Mute, () => A("volume_mute"))),
            Row(B("", Glyph.Previous, () => A("media_prev")), B("Přehrát / pauza", Glyph.Play, () => A("media_playpause")), B("", Glyph.Next, () => A("media_next"))),
            new TLabel("Ovládá Spotify nebo Hudbu, pokud běží.", dim: true)));

        // --- napájení ---
        flow.Controls.Add(MakeCard(Begin("Obrazovka a napájení", Glyph.Power),
            Row(B("Rozsvítit displej", Glyph.Brightness, () => A("display_wake")), B("Vypnout displej", Glyph.Moon, () => A("display_sleep"))),
            Row(B("Zamknout", Glyph.Lock, () => A("lock"))),
            Row(B("Uspat", Glyph.Moon, () => A("sleep")), B("Vynutit ukončení…", Glyph.Task, () => A("force_quit"))),
            Row(B("Odhlásit…", Glyph.SignOut, () => Confirmed("Odhlásit uživatele na Macu?", "logout")),
                B("Restartovat…", Glyph.Restart, () => Confirmed("Restartovat Mac? Spojení se přeruší.", "restart"))),
            Row(B("Vypnout Mac…", Glyph.Power, () => Confirmed("Vypnout Mac? Znovu ho na dálku nezapnete.", "shutdown"), ButtonKind.Danger))));

        // --- aplikace ---
        _appName.Items.AddRange(new object[] { "Safari", "Finder", "Terminal", "Music", "Notes", "Mail", "System Settings", "Activity Monitor" });
        _apps.DoubleClick += (_, _) => { if (_apps.SelectedItem is string s) A("open_app", s); };
        _apps.SelectedIndexChanged += (_, _) => { if (_apps.SelectedItem is string s) _appName.Text = s; };
        var cApps = Begin("Aplikace", Glyph.Apps);
        flow.Controls.Add(MakeCard(cApps,
            Row(Stretch(_appName, 6)),
            Row(B("Spustit / přepnout", Glyph.Play, () => { if (_appName.Text.Trim().Length > 0) A("open_app", _appName.Text.Trim()); }, ButtonKind.Primary),
                B("Ukončit", Glyph.Cancel, () => { if (_appName.Text.Trim().Length > 0) A("quit_app", _appName.Text.Trim()); })),
            new TLabel("Běžící aplikace (dvojklik = přepnout):", dim: true),
            Stretch(Framed(_apps), 6),
            B("Načíst běžící aplikace", Glyph.Refresh, () => A("list_apps"), ButtonKind.Ghost)));

        // --- schránka ---
        _chkClipSync.Checked = _settings.ClipboardSync;
        _chkClipSync.CheckedChanged += (_, _) =>
        {
            _settings.ClipboardSync = _chkClipSync.Checked;
            _settings.Save();
            if (_chkClipSync.Checked) StartClipSync(); else StopClipSync();
        };
        flow.Controls.Add(MakeCard(Begin("Schránka", Glyph.Paste),
            _chkClipSync,
            new TLabel("Co zkopírujete na jednom počítači, vložíte na druhém (Ctrl+C / Cmd+V). Jen text.", dim: true),
            Row(B("Napsat text ze schránky", Glyph.Keyboard, TypeOnMac)),
            new TLabel("Vepíše text ze schránky tohoto PC do aktivního okna – i když se liší rozložení klávesnice.", dim: true)));

        // --- ostatní ---
        var cOther = Begin("Ostatní", Glyph.Globe);
        var openBtn = B("Otevřít", Glyph.Open, () => A("open", _urlText.Text.Trim()));
        flow.Controls.Add(MakeCard(cOther,
            Row(Stretch(_urlText, 6 + openBtn.Width + 6), openBtn),
            Row(Stretch(_notifyText, 6)),
            Row(B("Oznámení", Glyph.Message, () => A("notify", _notifyText.Text)), B("Říct nahlas", Glyph.Speech, () => A("say", _notifyText.Text))),
            Row(B("Mission Control", Glyph.Apps, () => A("mission_control"), ButtonKind.Ghost))));

        // log dole
        var logCard = new Card { Text = "Průběh", Glyph = Glyph.Info, Dock = DockStyle.Fill, Margin = Padding.Empty };
        logCard.Controls.Add(_log);
        var logHost = new Panel { Dock = DockStyle.Bottom, Height = 170, Padding = new Padding(20, 0, 20, 16), BackColor = Color.Transparent };
        logHost.Controls.Add(logCard);

        // všechny karty stejně široké jako panel (bez vodorovného posuvníku), texty se zalomí
        void LayoutCards()
        {
            int w = flow.ClientSize.Width - flow.Padding.Horizontal - 16; // okraje karty 8 + 8
            if (w < 220) return;
            flow.SuspendLayout();
            foreach (var card in cards)
            {
                card.MinimumSize = new Size(w, 0);
                card.MaximumSize = new Size(w, 0);
                int inner = w - card.Padding.Horizontal;
                foreach (var (c, reserved) in stretch[card]) c.Width = Math.Max(80, inner - reserved);
                foreach (var label in Descendants(card).OfType<TLabel>()) label.MaximumSize = new Size(inner - 6, 0);
            }
            flow.ResumeLayout(true);
            flow.HorizontalScroll.Visible = false;
        }
        flow.ClientSizeChanged += (_, _) => LayoutCards();

        // Průběh: stejné okraje jako karty nad ním
        logHost.Padding = new Padding(12, 0, 12 + SystemInformation.VerticalScrollBarWidth, 12);

        _pageControl.Controls.Add(flow);
        _pageControl.Controls.Add(logHost);
        flow.BringToFront();
        LayoutCards();
    }

    private static IEnumerable<Control> Descendants(Control c)
    {
        foreach (Control child in c.Controls)
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private void InitControl()
    {
        _client.PostJson(MsgType.Action, new ActionRequest("volume_get"));
        _clipTimer.Tick += (_, _) => CheckLocalClipboard();
        if (_settings.ClipboardSync) StartClipSync();
    }

    // ---------------- automatické sdílení schránky ----------------

    private readonly ToggleSwitch _chkClipSync = new() { Text = "Sdílet schránku automaticky" };
    private readonly System.Windows.Forms.Timer _clipTimer = new() { Interval = 500 };
    private string? _clipLast;   // text, který mají obě strany stejný
    private uint _clipSeq;

    private static uint ClipSeq() => global::RemoteScreen.Client.Host.WinNative.GetClipboardSequenceNumber();

    private void StartClipSync()
    {
        _client.Post(MsgType.ClipboardWatch, new byte[] { 1 });
        _clipSeq = 0; // první kontrola pošle aktuální schránku tohoto PC
        _clipTimer.Start();
        CheckLocalClipboard();
    }

    private void StopClipSync()
    {
        _clipTimer.Stop();
        _client.Post(MsgType.ClipboardWatch, new byte[] { 0 });
    }

    /// <summary>Schránka tohoto PC se změnila → poslat text druhému počítači.</summary>
    private void CheckLocalClipboard()
    {
        uint seq = ClipSeq();
        if (seq == _clipSeq) return;
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { return; } // schránku má zrovna otevřenou jiná aplikace – zkusit příště
        _clipSeq = seq;
        if (text.Length == 0 || text.Length > ClipboardText.MaxAutoLength || ClipboardText.Same(text, _clipLast)) return;
        _clipLast = text;
        _client.PostJson(MsgType.ClipboardSet, new ClipboardText(_targetWindows ? text : text.Replace("\r\n", "\n"), Auto: true));
    }

    /// <summary>Schránka druhého počítače se změnila → převzít do Windows.</summary>
    private void OnAutoClipboard(string text)
    {
        if (!_settings.ClipboardSync || ClipboardText.Same(text, _clipLast)) return;
        _clipLast = text;
        try
        {
            Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
            _clipSeq = ClipSeq(); // vlastní změnu neposílat zpátky
        }
        catch { /* schránka zamčená jinou aplikací */ }
    }

    /// <summary>Vepíše text ze schránky tohoto PC do aktivního okna na druhém počítači.</summary>
    private void TypeOnMac()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
        catch { text = ""; }
        if (text.Length == 0) { _view.ShowToast("Schránka neobsahuje text.", Theme.Warning); return; }
        ReleaseKeys();
        _client.Post(MsgType.Text, Bin.Utf8(text.Replace("\r\n", "\n")));
        LogControl($"Píšu {text.Length} znaků do aktivního okna…");
        _view.ShowToast($"Píšu {text.Length} znaků…", Theme.Accent);
    }

    private void OnActionResult(ActionResult r)
    {
        if (r.Name == "list_apps" && r.Ok)
        {
            _apps.BeginUpdate();
            _apps.Items.Clear();
            foreach (var name in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderBy(x => x))
                _apps.Items.Add(name);
            _apps.EndUpdate();
            LogControl($"Běžící aplikace: {_apps.Items.Count}");
            return;
        }

        if (r.Name.StartsWith("volume_") && r.Ok && int.TryParse(r.Output, out var vol))
        {
            _volume.Value = Math.Clamp(vol, 0, 100);
            _lblVolume.Text = vol + " %";
            if (r.Name == "volume_get") return;
        }

        LogControl($"{ActionName(r.Name)}: {(r.Ok ? "OK" : "CHYBA")}{(r.Output.Length > 0 && r.Output != "OK" ? " – " + r.Output : "")}");
        if (!r.Ok) _view.ShowToast($"{ActionName(r.Name)}: {r.Output}", Theme.Danger);
    }

    private void OnClipboardData(ClipboardText c)
    {
        if (c.Auto) { OnAutoClipboard(c.Text); return; }
        _clipText.Text = c.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        LogControl($"Schránka z Macu načtena ({c.Text.Length} znaků)");
        if (_open != PanelKind.Control) _view.ShowToast($"Schránka načtena ({c.Text.Length} znaků) – je v panelu Ovládání", Theme.Accent);
    }

    private static string ActionName(string name) => name switch
    {
        "volume_up" => "Hlasitost +",
        "volume_down" => "Hlasitost −",
        "volume_mute" => "Ztlumení",
        "volume_set" => "Hlasitost",
        "sleep" => "Uspání",
        "display_sleep" => "Vypnutí displeje",
        "display_wake" => "Rozsvícení displeje",
        "lock" => "Zamknutí",
        "force_quit" => "Vynutit ukončení",
        "restart" => "Restart",
        "shutdown" => "Vypnutí",
        "logout" => "Odhlášení",
        "open_app" => "Spuštění aplikace",
        "quit_app" => "Ukončení aplikace",
        "open" => "Otevření",
        "notify" => "Oznámení",
        "say" => "Řeč",
        "clipboard_set" => "Schránka",
        "media_playpause" => "Přehrát/pauza",
        "media_next" => "Další skladba",
        "media_prev" => "Předchozí skladba",
        "mission_control" => "Mission Control",
        _ => name,
    };

    private void LogControl(string text)
    {
        if (_log.TextLength > 100_000) _log.Clear();
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
    }
}
