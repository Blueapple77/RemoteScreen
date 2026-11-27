using Microsoft.Win32;
using RemoteScreen.Client.Host;
using RemoteScreen.Client.UI;
using RemoteScreen.Host;

namespace RemoteScreen.Client;

/// <summary>RemoteScreen ve Windows: ikona u hodin (sdílení tohoto PC) + okna pro ovládání jiných počítačů.
/// Aplikace běží, dokud je otevřené okno nebo zapnuté sdílení.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly ClientSettings _settings;
    private readonly Control _ui = new();
    private readonly ServerHost _host;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private ConnectForm? _connect;
    private MainForm? _main;
    private bool _backgroundHintShown;
    private bool _quitting;

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteScreen");

    public TrayApp(ClientSettings settings, bool startInBackground)
    {
        _settings = settings;
        _ui.CreateControl(); // okno pro volání z jiných vláken (schránka, koš…)

        ServerConfig.Dir = Path.Combine(DataDir, "host");
        Log.FilePath = Path.Combine(DataDir, "host.log");
        Log.UseFile();

        _host = new ServerHost(new WinPlatform(_ui, Notify));
        _host.StateChanged += () => UiPost(UpdateTray);

        _menu.Renderer = new ToolStripProfessionalRenderer(new TrayColors());
        _menu.Opening += (_, _) => BuildMenu();
        _tray = new NotifyIcon
        {
            Icon = Program.AppIcon ?? SystemIcons.Application,
            Text = "RemoteScreen",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        BuildMenu();

        if (_settings.SharingEnabled) EnableSharing(interactive: false);
        if (!startInBackground) ShowWindow();
        UpdateTray();
    }

    private void UiPost(Action a)
    {
        try { if (_ui.IsHandleCreated) _ui.BeginInvoke(a); } catch (InvalidOperationException) { }
    }

    private void Notify(string text) => UiPost(() => _tray.ShowBalloonTip(5000, "RemoteScreen", text, ToolTipIcon.Info));

    // ---------------- okna ----------------

    public void ShowWindow()
    {
        if (_main != null)
        {
            if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
            _main.Activate();
            return;
        }
        if (_connect != null)
        {
            if (_connect.WindowState == FormWindowState.Minimized) _connect.WindowState = FormWindowState.Normal;
            _connect.Activate();
            return;
        }
        _connect = new ConnectForm(_settings, () => _host.Running);
        _connect.FormClosed += (_, _) =>
        {
            var c = _connect;
            _connect = null;
            if (c?.DialogResult == DialogResult.OK && c.Client != null)
            {
                _main = new MainForm(c.Client, _settings);
                _main.FormClosed += (_, _) =>
                {
                    var m = _main;
                    _main = null;
                    _settings.Save();
                    if (m?.ShowConnectAfterClose == true) ShowWindow();
                    else MaybeExit();
                };
                _main.Show();
            }
            else MaybeExit();
        };
        _connect.Show();
    }

    /// <summary>Po zavření okna: se zapnutým sdílením zůstat u hodin, jinak skončit.</summary>
    private void MaybeExit()
    {
        if (_quitting) return;
        if (_host.Running)
        {
            if (!_backgroundHintShown)
            {
                _backgroundHintShown = true;
                Notify("RemoteScreen běží dál u hodin, protože je zapnuté ovládání tohoto PC. Ukončíte ho kliknutím pravým tlačítkem na ikonu.");
            }
            return;
        }
        Quit();
    }

    private void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _host.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _settings.Save();
        _main?.Close();
        _connect?.Close();
        ExitThread();
    }

    // ---------------- sdílení ----------------

    private void EnableSharing(bool interactive)
    {
        var cfg = ServerConfig.Load();
        if (!cfg.HasPassword)
        {
            if (!interactive) return;
            string? pw = AskNewPassword(firstTime: true);
            if (pw == null) return;
            cfg.SetPassword(pw);
        }
        if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = Environment.MachineName;
        cfg.Save();

        _host.Start();
        if (!_host.Running)
        {
            if (interactive)
                MessageBox.Show("Ovládání tohoto PC nejde zapnout:\n\n" + _host.LastError, "RemoteScreen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _settings.SharingEnabled = true;
        _settings.Save();
        if (interactive)
            Notify("Ovládání tohoto PC je zapnuté. Pokud se Windows zeptá na povolení ve firewallu, povolte RemoteScreen v soukromé síti.");
    }

    private void DisableSharing()
    {
        _host.Stop();
        _settings.SharingEnabled = false;
        _settings.Save();
        if (_main == null && _connect == null) Quit();
    }

    private string? AskNewPassword(bool firstTime)
    {
        while (true)
        {
            var p1 = Dialogs.Prompt(null, "RemoteScreen – heslo",
                firstTime
                    ? "Zvolte heslo, kterým se k tomuto PC budou připojovat jiné počítače (alespoň 6 znaků):"
                    : "Nové heslo pro připojení k tomuto PC (alespoň 6 znaků):", "", password: true);
            if (p1 == null) return null;
            if (p1.Length < 6)
            {
                MessageBox.Show("Heslo je příliš krátké – musí mít alespoň 6 znaků.", "RemoteScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
                continue;
            }
            var p2 = Dialogs.Prompt(null, "RemoteScreen – heslo", "Zadejte heslo ještě jednou pro kontrolu:", "", password: true);
            if (p2 == null) return null;
            if (p1 == p2) return p1;
            MessageBox.Show("Hesla se neshodují, zkuste to znovu.", "RemoteScreen", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void ChangePassword()
    {
        var pw = AskNewPassword(firstTime: false);
        if (pw == null) return;
        var cfg = ServerConfig.Load();
        cfg.SetPassword(pw);
        cfg.Save();
        if (_host.Running) _host.Restart();
        Notify("Heslo bylo změněno.");
    }

    // ---------------- spouštění s Windows ----------------

    private static bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("RemoteScreen") != null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("RemoteScreen", $"\"{Application.ExecutablePath}\" --background");
            else key.DeleteValue("RemoteScreen", false);
        }
    }

    // ---------------- ikona a menu ----------------

    private void UpdateTray()
    {
        var sessions = _host.Sessions;
        string text = !_host.Running ? "RemoteScreen"
            : sessions.Length == 0 ? "RemoteScreen – ovládání PC zapnuto"
            : "RemoteScreen – PC ovládá " + string.Join(", ", sessions.Select(s => s.ClientName));
        _tray.Text = text.Length > 120 ? text[..120] : text;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var bold = new Font(Theme.Body, FontStyle.Bold);
        _menu.Font = Theme.Body;

        var open = new ToolStripMenuItem("Otevřít RemoteScreen", null, (_, _) => ShowWindow()) { Font = bold };
        _menu.Items.Add(open);
        _menu.Items.Add(new ToolStripSeparator());

        var share = new ToolStripMenuItem("Povolit ovládání tohoto PC") { Checked = _host.Running };
        share.Click += (_, _) => { if (_host.Running) DisableSharing(); else EnableSharing(interactive: true); UpdateTray(); };
        _menu.Items.Add(share);

        if (_host.Running)
        {
            Info($"●  Zapnuto · port {_host.Config.Port}");
            foreach (var ip in ServerHost.LocalAddresses().Take(3)) Info("     " + ip);
            var sessions = _host.Sessions;
            if (sessions.Length == 0) Info("     Nikdo není připojen");
            else
            {
                foreach (var s in sessions) Info($"     🖥  {s.ClientName} · {s.Address} · od {s.Since:HH:mm}");
                _menu.Items.Add(new ToolStripMenuItem("Odpojit všechny", null, (_, _) => _host.DisconnectAll()));
            }
        }
        else if (_host.LastError != null)
        {
            Info("⚠  " + _host.LastError);
        }

        _menu.Items.Add(new ToolStripMenuItem(ServerConfig.Load().HasPassword ? "Změnit heslo…" : "Nastavit heslo…", null, (_, _) => ChangePassword()));
        if (_host.Fingerprint.Length > 0)
            _menu.Items.Add(new ToolStripMenuItem("Kopírovat otisk certifikátu", null, (_, _) => Clipboard.SetText(_host.Fingerprint)));
        _menu.Items.Add(new ToolStripMenuItem("Zobrazit log", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); } catch { }
        }));
        _menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Spouštět s Windows") { Checked = AutostartEnabled };
        autostart.Click += (_, _) => { try { AutostartEnabled = !AutostartEnabled; } catch (Exception ex) { MessageBox.Show(ex.Message, "RemoteScreen"); } };
        _menu.Items.Add(autostart);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Ukončit RemoteScreen", null, (_, _) => Quit()));

        void Info(string t) => _menu.Items.Add(new ToolStripMenuItem(t) { Enabled = false });
    }

    /// <summary>Barvy kontextového menu podle motivu.</summary>
    private sealed class TrayColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Surface;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color MenuItemSelected => Theme.SurfaceHover;
        public override Color MenuItemBorder => Theme.SurfaceHover;
        public override Color MenuBorder => Theme.Border;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
        public override Color CheckBackground => Theme.SurfaceHover;
        public override Color CheckSelectedBackground => Theme.SurfaceHover;
        public override Color CheckPressedBackground => Theme.SurfaceHover;
    }
}
