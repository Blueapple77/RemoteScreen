using System.Drawing.Drawing2D;
using System.Net.Sockets;
using System.Security.Authentication;
using RemoteScreen.Client.UI;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

/// <summary>Přihlašovací okno v návrhu C: poloprůhledné sklo, dlaždice počítačů, heslo, oranžové Připojit.</summary>
public sealed class ConnectForm : GlassForm
{
    private const int W = 460, Margin_ = 18, Pad = 26, X0 = Margin_ + Pad, Inner = W - 2 * X0;
    private const int TileH = 88, Gap = 10, MaxTiles = 6;

    private readonly ClientSettings _settings;
    private readonly Func<bool> _sharingOn;
    private readonly InputBox _password = new() { Password = true, Placeholder = "Heslo nastavené na druhém počítači" };
    private readonly FlatButton _connect = new() { Text = "Připojit", Kind = ButtonKind.Primary, AutoFit = false, Radius = 14 };
    private readonly FlatButton _discover = new() { Text = "Hledat počítače", Kind = ButtonKind.Secondary, Glyph = Glyph.Search, AutoFit = false };
    private readonly FlatButton _addIp = new() { Text = "Přidat pomocí IP", Kind = ButtonKind.Secondary, Glyph = Glyph.Connect, AutoFit = false };
    private readonly TLabel _pwLabel = new("Heslo", dim: true);
    private readonly Label _status = new() { AutoSize = false, ForeColor = Theme.TextDim, Font = Theme.Small, BackColor = Color.Transparent };
    private readonly Label _empty = new() { AutoSize = false, ForeColor = Theme.TextDim, Font = Theme.Small, BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleCenter };
    private readonly List<DeviceTile> _tiles = new();
    private readonly Dictionary<string, DiscoveredHost> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly Font _titleFont = new(Theme.Title.FontFamily, 15f, FontStyle.Bold);
    private readonly Font _brandFont = new(Theme.Small.FontFamily, 7.5f, FontStyle.Bold);
    private string? _selected;
    private bool _busy, _searching;
    private Rectangle _footer;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public RemoteClient? Client { get; private set; }

    public ConnectForm(ClientSettings settings, Func<bool>? sharingOn = null)
    {
        _settings = settings;
        _sharingOn = sharingOn ?? (() => false);
        Text = "RemoteScreen";
        Icon = Program.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;

        _connect.Font = new Font(Theme.BodyBold.FontFamily, 11f, FontStyle.Bold);
        _password.Height = 44;
        _pwLabel.Font = Theme.Small;
        _pwLabel.AutoSize = false;
        Controls.AddRange(new Control[] { _empty, _pwLabel, _password, _connect, _discover, _addIp, _status });

        _selected = _settings.RecentHosts.FirstOrDefault();
        _connect.Click += async (_, _) => await ConnectAsync();
        _discover.Click += async (_, _) => await DiscoverAsync();
        _addIp.Click += (_, _) => AddByIp();
        _password.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await ConnectAsync(); }
        };
        Shown += (_, _) =>
        {
            _password.Box.Focus();
            _ = DiscoverAsync();
        };
        RebuildTiles();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyWindowChrome(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _titleFont.Dispose(); _brandFont.Dispose(); }
        base.Dispose(disposing);
    }

    // ---------------- vzhled ----------------

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // skleněná karta
        var card = new RectangleF(Margin_, Margin_, ClientSize.Width - 2 * Margin_, ClientSize.Height - 2 * Margin_);
        Theme.FillRound(g, Color.FromArgb(214, 22, 22, 26), card, 22);
        Theme.DrawRound(g, Color.FromArgb(28, 255, 255, 255), card, 22);

        // hlavička
        var icon = new RectangleF(X0, X0, 40, 40);
        Theme.FillRound(g, Theme.AccentA(40), icon, 12);
        Theme.DrawRound(g, Theme.AccentA(115), icon, 12);
        Theme.DrawGlyph(g, Glyph.Desktop, 14f, Theme.AccentText, Rectangle.Round(icon));
        TextRenderer.DrawText(g, "R E M O T E S C R E E N", _brandFont, new Rectangle(X0 + 52, X0 + 1, Inner - 52, 16), Theme.AccentText,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "Kam se chcete připojit?", _titleFont, new Rectangle(X0 + 50, X0 + 15, Inner - 50, 28), Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);

        // stav sdílení tohoto PC
        bool on = _sharingOn();
        Theme.FillRound(g, Color.FromArgb(13, 255, 255, 255), _footer, 14);
        Theme.DrawGlyph(g, Glyph.Desktop, 11f, on ? Theme.Success : Theme.TextDim, new Rectangle(_footer.X + 8, _footer.Y, 28, _footer.Height));
        TextRenderer.DrawText(g, on
                ? "Toto PC lze ovládat z jiného počítače · sdílení zapnuto"
                : "Ovládání tohoto PC je vypnuté (zapnete u ikony vedle hodin)",
            Theme.Small, new Rectangle(_footer.X + 38, _footer.Y, _footer.Width - 46, _footer.Height), Theme.TextDim,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    /// <summary>Rozmístí prvky podle počtu dlaždic a nastaví výšku okna.</summary>
    private void LayoutAll()
    {
        int y = X0 + 60;
        int tileW = (Inner - Gap) / 2;
        int shown = Math.Min(_tiles.Count, MaxTiles);
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            t.Visible = i < shown;
            t.SetBounds(X0 + (i % 2) * (tileW + Gap), y + (i / 2) * (TileH + Gap), tileW, TileH);
        }
        int rows = Math.Max(1, (shown + 1) / 2);
        _empty.Visible = shown == 0;
        _empty.SetBounds(X0, y, Inner, TileH);
        y += rows * TileH + (rows - 1) * Gap + 18;

        _pwLabel.SetBounds(X0, y, Inner, 18);
        y += 22;
        _password.SetBounds(X0, y, Inner, 44);
        y += 44 + 14;
        _connect.SetBounds(X0, y, Inner, 48);
        y += 48 + 12;
        int half = (Inner - 8) / 2;
        _discover.Height = _addIp.Height = 36;
        _discover.SetBounds(X0, y, half, 36);
        _addIp.SetBounds(X0 + half + 8, y, Inner - half - 8, 36);
        y += 36 + 8;
        _status.SetBounds(X0, y, Inner, 40);
        y += 40 + 6;
        _footer = new Rectangle(X0, y, Inner, 40);
        y += 40 + Pad + Margin_;
        ClientSize = new Size(W, y);
        Invalidate();
    }

    // ---------------- dlaždice ----------------

    private void RebuildTiles()
    {
        foreach (var t in _tiles) { Controls.Remove(t); t.Dispose(); }
        _tiles.Clear();

        var entries = new List<(string Address, string Name, string? Platform, bool Online)>();
        foreach (var d in _discovered.Values) entries.Add((d.Address, d.Name, d.Platform, true));
        foreach (var h in _settings.RecentHosts)
        {
            if (_discovered.ContainsKey(h)) continue;
            _settings.Hosts.TryGetValue(h, out var info);
            entries.Add((h, info?.Name ?? h, info?.Platform, false));
        }
        if (_selected == null || !entries.Any(e => e.Address.Equals(_selected, StringComparison.OrdinalIgnoreCase)))
            _selected = entries.FirstOrDefault().Address;

        foreach (var e in entries.Take(MaxTiles))
        {
            bool canWake = _settings.WakeMacs.ContainsKey(e.Address);
            string platform = e.Platform == "windows" ? "Windows" : "macOS";
            var tile = new DeviceTile
            {
                Text = e.Name,
                Address = e.Address,
                Online = e.Online,
                Icon = e.Platform == "windows" ? Glyph.Desktop : Glyph.Laptop,
                Subtitle = e.Online ? $"online · {platform}" : canWake ? "spí · probudit" : $"{e.Address} · nenalezen",
                SubtitleAccent = !e.Online && canWake,
                Selected = e.Address.Equals(_selected, StringComparison.OrdinalIgnoreCase),
            };
            new ToolTip().SetToolTip(tile, $"{e.Name}\n{e.Address} · {platform}");
            string address = e.Address;
            tile.Click += (_, _) => Select(address);
            tile.DoubleClick += async (_, _) => { Select(address); await ConnectAsync(); };
            tile.KeyDown += async (_, k) => { if (k.KeyCode == Keys.Enter) { Select(address); await ConnectAsync(); } };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Probudit (Wake-on-LAN)…", null, async (_, _) => { Select(address); await WakeAsync(address, PortFor(address)); });
            menu.Items.Add("Odebrat ze seznamu", null, (_, _) =>
            {
                _settings.ForgetHost(address);
                _settings.Save();
                _discovered.Remove(address);
                RebuildTiles();
            });
            tile.ContextMenuStrip = menu;

            _tiles.Add(tile);
            Controls.Add(tile);
        }
        _empty.Text = _searching
            ? "Hledám počítače v místní síti…"
            : "Zatím žádný počítač. Na druhém počítači zapněte RemoteScreen, nebo použijte Přidat pomocí IP.";
        LayoutAll();
    }

    private void Select(string address)
    {
        _selected = address;
        foreach (var t in _tiles) t.Selected = t.Address.Equals(address, StringComparison.OrdinalIgnoreCase);
        _password.Box.Focus();
    }

    private int PortFor(string address) =>
        _discovered.TryGetValue(address, out var d) ? d.Port :
        _settings.Hosts.TryGetValue(address, out var h) ? h.Port : _settings.Port;

    private string NameFor(string address) =>
        _discovered.TryGetValue(address, out var d) ? d.Name :
        _settings.Hosts.TryGetValue(address, out var h) ? h.Name : address;

    // ---------------- stav ----------------

    private void SetStatus(string text, Color? color = null)
    {
        _status.ForeColor = color ?? Theme.TextDim;
        _status.Text = text;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _connect.Enabled = _addIp.Enabled = _password.Enabled = !busy;
        _discover.Enabled = !busy && !_searching;
        _connect.Text = busy ? "Připojuji…" : "Připojit";
        UseWaitCursor = busy;
    }

    // ---------------- připojení ----------------

    private async Task ConnectAsync()
    {
        if (_busy) return;
        string? host = _selected;
        if (host == null)
        {
            SetStatus("Vyberte počítač, nebo ho přidejte pomocí IP.", Theme.Danger);
            return;
        }
        if (_password.Text.Length == 0)
        {
            SetStatus("Zadejte heslo.", Theme.Danger);
            _password.Box.Focus();
            return;
        }
        int port = PortFor(host);
        string name = NameFor(host);

        SetBusy(true);
        SetStatus($"Připojuji se k {name}…");
        var client = new RemoteClient();
        try
        {
            await client.ConnectAsync(host, port, _password.Text, (fp, known) => AskTrust(host, name, fp, known),
                h => _settings.KnownHosts.TryGetValue(h, out var f) ? f : null, CancellationToken.None);

            _settings.KnownHosts[host] = client.Fingerprint;
            if (client.Info.MacAddresses is { Length: > 0 } macs) _settings.WakeMacs[host] = macs.ToList();
            _settings.Hosts[host] = new HostInfo(client.Info.HostName ?? name, client.Info.Platform ?? "macos", port);
            _settings.RememberHost(host);
            _settings.Port = port;
            _settings.Save();

            Client = client;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            client.Dispose();
            SetBusy(false);
            bool unreachable = ex is SocketException or TimeoutException;
            if (unreachable && _settings.WakeMacs.ContainsKey(host) &&
                MessageBox.Show(this, $"{name} neodpovídá – možná spí.\n\nMám ho probudit přes síť (Wake-on-LAN) a připojit se, až naběhne?",
                    "RemoteScreen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                await WakeAsync(host, port);
                return;
            }
            SetStatus(ex switch
            {
                SocketException se => $"Nelze se připojit ({se.SocketErrorCode}). Běží na druhém počítači RemoteScreen se zapnutým ovládáním a jste ve stejné síti?",
                TimeoutException te => te.Message + " Je počítač zapnutý a ve stejné síti?",
                AuthenticationException ae => ae.Message,
                _ => ex.Message,
            }, Theme.Danger);
            _password.Box.SelectAll();
            _password.Box.Focus();
        }
    }

    private bool AskTrust(string host, string name, string fingerprint, string? known)
    {
        string text;
        MessageBoxIcon icon;
        if (known == null)
        {
            bool matchesDiscovery = _discovered.TryGetValue(host, out var d) &&
                                    string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
            text = $"Připojujete se k {name} ({host}) poprvé.\n\nOtisk certifikátu:\n{fingerprint}\n\n" +
                   "Můžete ho porovnat s otiskem na druhém počítači (menu RemoteScreen → Kopírovat otisk certifikátu).\n" +
                   (matchesDiscovery ? "(Shoduje se s otiskem z vyhledávání v síti.)\n" : "") +
                   "\nDůvěřovat tomuto počítači?";
            icon = MessageBoxIcon.Question;
        }
        else
        {
            text = $"POZOR: certifikát počítače {name} ({host}) se změnil!\n\nUložený otisk:\n{known}\n\nNový otisk:\n{fingerprint}\n\n" +
                   "Pokud jste RemoteScreen na druhém počítači nepřeinstalovali, může jít o útok. Přesto pokračovat?";
            icon = MessageBoxIcon.Warning;
        }
        return MessageBox.Show(this, text, "Ověření počítače", MessageBoxButtons.YesNo, icon,
            known == null ? MessageBoxDefaultButton.Button1 : MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }

    // ---------------- hledání a přidání ----------------

    private async Task DiscoverAsync()
    {
        if (_searching) return;
        _searching = true;
        _discover.Enabled = false;
        SetStatus("Hledám počítače v místní síti…");
        if (_tiles.Count == 0) RebuildTiles();
        try
        {
            var found = await Task.Run(() => Discovery.FindAsync(TimeSpan.FromSeconds(1.6)));
            _discovered.Clear();
            foreach (var d in found)
            {
                _discovered[d.Address] = d;
                if (d.MacAddresses.Length > 0) _settings.WakeMacs[d.Address] = d.MacAddresses.ToList();
            }
            _settings.Save();
            _searching = false;
            if (_selected == null || (!_discovered.ContainsKey(_selected) && found.Count > 0 && !_settings.RecentHosts.Contains(_selected)))
                _selected = found.FirstOrDefault()?.Address ?? _selected;
            RebuildTiles();
            SetStatus(found.Count switch
            {
                0 => "Žádný počítač nenalezen. Použijte Přidat pomocí IP – adresu najdete v menu RemoteScreen na druhém počítači.",
                1 => "Nalezen 1 počítač.",
                < 5 => $"Nalezeny {found.Count} počítače.",
                _ => $"Nalezeno {found.Count} počítačů.",
            }, found.Count > 0 ? Theme.Success : null);
        }
        catch (Exception ex)
        {
            _searching = false;
            RebuildTiles();
            SetStatus("Vyhledávání selhalo: " + ex.Message, Theme.Danger);
        }
        finally
        {
            _searching = false;
            _discover.Enabled = !_busy;
        }
    }

    private void AddByIp()
    {
        var input = Dialogs.Prompt(this, "Přidat pomocí IP",
            "IP adresa počítače (najdete ji v menu ikony RemoteScreen na druhém počítači), případně s portem – např. 192.168.1.20 nebo 192.168.1.20:47800:", "");
        if (string.IsNullOrWhiteSpace(input)) return;
        string text = input.Trim();
        string host = text;
        int port = ProtocolInfo.DefaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int p) && p is > 0 and < 65536)
        {
            host = text[..colon];
            port = p;
        }
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            SetStatus("Neplatná adresa: " + text, Theme.Danger);
            return;
        }
        if (!_settings.Hosts.ContainsKey(host)) _settings.Hosts[host] = new HostInfo(host, null, port);
        _settings.RememberHost(host);
        _settings.Save();
        _selected = host;
        RebuildTiles();
        SetStatus($"Přidáno {host} – zadejte heslo a připojte se.", Theme.Success);
        _password.Box.Focus();
    }

    // ---------------- probuzení (Wake-on-LAN) ----------------

    private async Task WakeAsync(string host, int port)
    {
        if (!_settings.WakeMacs.TryGetValue(host, out var macs) || macs.Count == 0)
        {
            var input = Dialogs.Prompt(this, "Probudit počítač",
                "MAC adresu tohoto počítače zatím neznám – uloží se sama při prvním připojení. " +
                "Můžete ji zadat ručně (najdete ji v menu RemoteScreen na druhém počítači u adresy):", "");
            if (input == null) return;
            if (!WakeOnLan.TryParseMac(input, out var parsed))
            {
                SetStatus("Neplatná MAC adresa – má 12 šestnáctkových číslic, např. A1:B2:C3:D4:E5:F6.", Theme.Danger);
                return;
            }
            macs = new List<string> { WakeOnLan.Format(parsed) };
            _settings.WakeMacs[host] = macs;
            _settings.Save();
        }

        SetBusy(true);
        bool awake = false;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var lastPacket = TimeSpan.FromSeconds(-100);
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (sw.Elapsed - lastPacket > TimeSpan.FromSeconds(8))
                {
                    await WakeOnLan.SendAsync(macs, host); // opakovat – první paket se občas ztratí
                    lastPacket = sw.Elapsed;
                }
                SetStatus($"Probouzím {NameFor(host)}… čekám na odpověď ({(int)sw.Elapsed.TotalSeconds} s)");
                if (await IsReachableAsync(host, port)) { awake = true; break; }
                await Task.Delay(1500);
            }
        }
        catch (Exception ex)
        {
            SetBusy(false);
            SetStatus("Probouzecí signál nelze odeslat: " + ex.Message, Theme.Danger);
            return;
        }
        SetBusy(false);

        if (!awake)
        {
            SetStatus("Počítač neodpověděl. Na Macu musí být zapnuto Nastavení → Energie → Probudit při přístupu k síti " +
                      "(spolehlivě funguje přes síťový kabel; vypnutý počítač probudit nejde).", Theme.Danger);
            return;
        }

        SetStatus("Počítač je vzhůru.", Theme.Success);
        if (_password.Text.Length > 0) await ConnectAsync();
        else _password.Box.Focus();
    }

    private static async Task<bool> IsReachableAsync(string host, int port)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
