using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Přihlášení k jinému počítači (návrh C – poloprůhledné tmavé okno, oranžový akcent).</summary>
internal sealed class ConnectWindow : IWindowHandler
{
    private static ConnectWindow? _instance;

    private const double W = 460, Pad = 36, Inner = W - 2 * Pad;
    private const double TileH = 88, Gap = 10;
    private const int MaxTiles = 6;

    private readonly MacClientSettings _settings = MacClientSettings.Load();
    private readonly Dictionary<string, DiscoveredHost> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IntPtr> _rowControls = new();
    private IntPtr _window, _root, _list, _pwLabel, _password, _connect, _search, _addIp, _status, _footer, _empty;
    private string? _selected;
    private bool _busy, _searching, _closed;
    private CancellationTokenSource? _connectCts;

    private sealed record Entry(string Address, int Port, string Name, string? Platform, bool Online, bool Saved);

    public static void Show()
    {
        if (_instance != null && !_instance._closed)
        {
            UI.ShowWindow(_instance._window);
            return;
        }
        _instance = new ConnectWindow();
        UI.SetAccent(_instance._settings.Accent);
        _instance.Build();
        UI.ShowWindow(_instance._window);
        _ = _instance.SearchAsync();
    }

    // ---------------- sestavení okna ----------------

    private void Build()
    {
        const double h0 = 600;
        _window = UI.CreateWindow(new CGRect(0, 0, W, h0),
            UI.StyleTitled | UI.StyleClosable | UI.StyleMiniaturizable | UI.StyleFullSizeContent, "RemoteScreen", this);
        ObjC.SetBool(_window, "setTitlebarAppearsTransparent:", true);
        ObjC.SetInt(_window, "setTitleVisibility:", 1); // skrytý titulek
        ObjC.SetBool(_window, "setMovableByWindowBackground:", true);
        ObjC.SetBool(_window, "setOpaque:", false);
        ObjC.Set(_window, "setBackgroundColor:", UI.Clear);

        // sklo přes celé okno (průhlednost za oknem) + jemný tmavý nádech
        IntPtr glass = UI.Glass(new CGRect(0, 0, W, h0), 13 /* HUDWindow */, behindWindow: true);
        ObjC.Set(_window, "setContentView:", glass);
        IntPtr tint = ObjC.NewWithFrame("NSView", new CGRect(0, 0, W, h0));
        UI.Rounded(tint, 0, ObjC.Color(0.06, 0.06, 0.09, 0.35));
        UI.Autoresize(tint, 2 | 16);
        UI.Add(glass, tint);

        _root = ObjC.NewWithFrame(FlippedViewClass(), new CGRect(0, 0, W, h0));
        UI.Autoresize(_root, 2 | 16);
        UI.Add(glass, _root);

        // hlavička: oranžový čtvereček s ikonou + nadpis
        IntPtr badge = ObjC.NewWithFrame("NSView", new CGRect(0, 0, 40, 40));
        UI.Rounded(badge, 12, UI.AccentA(0.16), UI.AccentA(0.45));
        Place(_root, badge, Pad, 40, 40, 40);
        IntPtr badgeIcon = ObjC.NewWithFrame("NSImageView", new CGRect(8, 8, 24, 24));
        IntPtr monitor = UI.Symbol("display", 18);
        if (monitor != IntPtr.Zero) ObjC.Set(badgeIcon, "setImage:", monitor);
        if (ObjC.RespondsTo(badgeIcon, "setContentTintColor:")) ObjC.Set(badgeIcon, "setContentTintColor:", UI.AccentText);
        UI.Add(badge, badgeIcon);
        Place(_root, UI.Label("REMOTESCREEN", 11, UI.WeightBold, UI.AccentText), Pad + 52, 40, Inner - 52, 15);
        Place(_root, UI.Label("Kam se chcete připojit?", 20, UI.WeightBold, UI.Text), Pad + 52, 55, Inner - 52, 26);

        _list = ObjC.NewWithFrame(FlippedViewClass(), new CGRect(Pad, 100, Inner, TileH));
        UI.Add(_root, _list);
        _empty = UI.Label("", 12.5, UI.WeightRegular, UI.TextDim, wrap: true);
        ObjC.SetInt(_empty, "setAlignment:", 2);

        _pwLabel = UI.Label("Heslo", 12, UI.WeightMedium, UI.TextDim);
        UI.Add(_root, _pwLabel);
        _password = UI.TextField("Heslo nastavené na druhém počítači", secure: true);
        UI.Add(_root, _password);
        _connect = UI.Button("Připojit", () => _ = ConnectAsync(), primary: true);
        UI.Add(_root, _connect);
        _search = UI.Button("Hledat počítače", () => _ = SearchAsync());
        UI.Add(_root, _search);
        _addIp = UI.Button("Přidat pomocí IP", AddByIp);
        UI.Add(_root, _addIp);
        _status = UI.Label("", 12.5, UI.WeightRegular, UI.TextDim, wrap: true);
        UI.Add(_root, _status);

        // stav sdílení tohoto Macu (zaoblený proužek dole)
        _footer = ObjC.NewWithFrame(FlippedViewClass(), new CGRect(Pad, 0, Inner, 40));
        UI.Rounded(_footer, 14, ObjC.Color(1, 1, 1, 0.05));
        UI.Add(_root, _footer);
        UpdateFooter();

        _selected = _settings.LastHost;
        RebuildList();
        ObjC.Send(_window, ObjC.Sel("center"));
        ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _password);
    }

    /// <summary>Rozmístí prvky pod dlaždicemi a upraví výšku okna (horní okraj zůstává na místě).</summary>
    private void LayoutAll(int tiles)
    {
        int rows = Math.Max(1, (Math.Min(tiles, MaxTiles) + 1) / 2);
        double listH = rows * TileH + (rows - 1) * Gap;
        UI.Frame(_list, Pad, 100, Inner, listH);
        double y = 100 + listH + 18;
        UI.Frame(_pwLabel, Pad, y, Inner, 16); y += 20;
        UI.Frame(_password, Pad, y, Inner, 34); y += 46;
        UI.Frame(_connect, Pad - 2, y, Inner + 4, 44); y += 54;
        double half = (Inner - 10) / 2;
        UI.Frame(_search, Pad - 2, y, half + 2, 34);
        UI.Frame(_addIp, Pad + half + 10, y, half + 2, 34); y += 44;
        UI.Frame(_status, Pad, y, Inner, 34); y += 40;
        UI.Frame(_footer, Pad, y, Inner, 40); y += 40 + 30;

        var frame = ObjC.Rect(_window, "frame");
        if (Math.Abs(frame.H - y) > 0.5)
        {
            var nf = new CGRect(frame.X, frame.Y + frame.H - y, W, y);
            ObjC.SendRectBool(_window, ObjC.Sel("setFrame:display:"), nf, true);
        }
    }

    private static void Place(IntPtr parent, IntPtr view, double x, double y, double w, double h)
    {
        UI.Frame(view, x, y, w, h);
        UI.Add(parent, view);
    }

    private void UpdateFooter()
    {
        var host = MenuBarApp.Host;
        string ip = ServerHost.LocalAddresses().FirstOrDefault()?.Split(' ')[0] ?? "";
        IntPtr icon = ObjC.NewWithFrame("NSImageView", new CGRect(12, 11, 18, 18));
        IntPtr img = UI.Symbol("display", 13);
        if (img != IntPtr.Zero) ObjC.Set(icon, "setImage:", img);
        if (ObjC.RespondsTo(icon, "setContentTintColor:")) ObjC.Set(icon, "setContentTintColor:", host.Running ? UI.Success : UI.TextDim);
        UI.Add(_footer, icon);
        Place(_footer, UI.Label(host.Running
            ? $"Tento Mac lze ovládat z jiného počítače · {ip}"
            : "Ovládání tohoto Macu je vypnuté (menu RemoteScreen v liště)", 11.5, UI.WeightRegular, UI.TextDim), 38, 12, Inner - 48, 16);
    }

    // ---------------- seznam ----------------

    private List<Entry> Entries()
    {
        var list = new List<Entry>();
        foreach (var d in _discovered.Values)
        {
            var saved = _settings.Hosts.FirstOrDefault(h => h.Address.Equals(d.Address, StringComparison.OrdinalIgnoreCase));
            list.Add(new Entry(d.Address, d.Port, d.Name, d.Platform, true, saved != null));
        }
        foreach (var h in _settings.Hosts)
        {
            if (_discovered.ContainsKey(h.Address)) continue;
            list.Add(new Entry(h.Address, h.Port, h.Name, h.Platform, false, true));
        }
        return list;
    }

    private void RebuildList()
    {
        foreach (var c in _rowControls) UI.ForgetAction(c);
        _rowControls.Clear();
        foreach (var v in ToArray(ObjC.Get(_list, "subviews"))) ObjC.Send(v, ObjC.Sel("removeFromSuperview"));

        var entries = Entries();
        if (_selected == null || !entries.Any(e => e.Address.Equals(_selected, StringComparison.OrdinalIgnoreCase)))
            _selected = entries.FirstOrDefault()?.Address;

        if (entries.Count == 0)
        {
            UI.SetText(_empty, _searching
                ? "Hledám počítače v místní síti…"
                : "Zatím žádný počítač. Na druhém počítači zapněte RemoteScreen (na Windows „Povolit ovládání tohoto PC“), nebo použijte Přidat pomocí IP.");
            Place(_list, _empty, 8, 20, Inner - 16, 50);
        }
        double tileW = (Inner - Gap) / 2;
        int i = 0;
        foreach (var e in entries.Take(MaxTiles))
        {
            AddTile(e, (i % 2) * (tileW + Gap), (i / 2) * (TileH + Gap), tileW);
            i++;
        }
        LayoutAll(entries.Count);
    }

    /// <summary>Dlaždice počítače: ikona, tečka stavu, název, podtitulek (stejně jako na Windows).</summary>
    private void AddTile(Entry e, double x, double y, double w)
    {
        bool sel = e.Address.Equals(_selected, StringComparison.OrdinalIgnoreCase);
        IntPtr tile = ObjC.NewWithFrame(FlippedViewClass(), new CGRect(x, y, w, TileH));
        UI.Rounded(tile, 16,
            sel ? UI.AccentA(0.14) : ObjC.Color(1, 1, 1, 0.05),
            sel ? UI.AccentA(0.55) : ObjC.Color(1, 1, 1, 0.10));
        UI.Add(_list, tile);

        IntPtr icon = ObjC.NewWithFrame("NSImageView", new CGRect(14, 12, 26, 26));
        IntPtr img = UI.Symbol(e.Platform == "windows" ? "pc" : "laptopcomputer", 20);
        if (img == IntPtr.Zero) img = UI.Symbol("desktopcomputer", 20);
        if (img != IntPtr.Zero) ObjC.Set(icon, "setImage:", img);
        if (ObjC.RespondsTo(icon, "setContentTintColor:")) ObjC.Set(icon, "setContentTintColor:", sel ? UI.AccentText : UI.TextDim);
        UI.Add(tile, icon);

        if (e.Online)
        {
            IntPtr dot = ObjC.NewWithFrame("NSView", new CGRect(w - 22, 16, 8, 8));
            UI.Rounded(dot, 4, UI.Success);
            UI.Add(tile, dot);
        }

        IntPtr name = UI.Label(e.Name.Length > 0 ? e.Name : e.Address, 14, UI.WeightSemibold, UI.Text);
        ObjC.SetInt(name, "setLineBreakMode:", 4);
        Place(tile, name, 14, 44, w - 24, 19);
        bool canWake = _settings.WakeMacs.ContainsKey(e.Address);
        string platform = e.Platform == "windows" ? "Windows" : "macOS";
        string sub = e.Online ? $"online · {platform}" : canWake ? "spí · probudit" : $"{e.Address} · nenalezen";
        IntPtr subLabel = UI.Label(sub, 11.5, UI.WeightRegular, !e.Online && canWake ? UI.AccentText : UI.TextDim);
        ObjC.SetInt(subLabel, "setLineBreakMode:", 4);
        Place(tile, subLabel, 14, 64, w - 24, 16);

        // průhledné tlačítko přes celou dlaždici = výběr (dvojklik = připojit)
        IntPtr hit = ObjC.NewWithFrame("NSButton", new CGRect(0, 0, w, TileH));
        ObjC.SetBool(hit, "setTransparent:", true);
        ObjC.Set(hit, "setTitle:", ObjC.Str(""));
        ObjC.Set(hit, "setToolTip:", ObjC.Str($"{e.Name}\n{e.Address} · {platform}"));
        string address = e.Address;
        UI.OnClick(hit, () =>
        {
            IntPtr ev = ObjC.Get(ObjC.Get(ObjC.Cls("NSApplication"), "sharedApplication"), "currentEvent");
            bool dbl = ev != IntPtr.Zero && ObjC.SendRetInt(ev, ObjC.Sel("clickCount")) >= 2;
            _selected = address;
            RebuildList();
            ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _password);
            if (dbl) _ = ConnectAsync();
        });
        UI.Add(tile, hit);
        _rowControls.Add(hit);

        if (e.Saved && !e.Online)
        {
            IntPtr forget = UI.IconButton("xmark", "Odebrat ze seznamu", () =>
            {
                _settings.Forget(address);
                _settings.Save();
                RebuildList();
            });
            if (ObjC.RespondsTo(forget, "setContentTintColor:")) ObjC.Set(forget, "setContentTintColor:", UI.TextDim);
            Place(tile, forget, w - 36, 6, 30, 28);
            _rowControls.Add(forget);
        }
    }

    private static IEnumerable<IntPtr> ToArray(IntPtr nsArray)
    {
        nuint n = ObjC.SendRetUInt(nsArray, ObjC.Sel("count"));
        var list = new List<IntPtr>();
        for (nuint i = 0; i < n; i++) list.Add(ObjC.SendRetObjInt(nsArray, ObjC.Sel("objectAtIndex:"), i));
        return list;
    }

    private Entry? Selected() =>
        Entries().FirstOrDefault(e => e.Address.Equals(_selected, StringComparison.OrdinalIgnoreCase));

    // ---------------- hledání ----------------

    private async Task SearchAsync()
    {
        if (_searching) return;
        _searching = true;
        SetStatus("Hledám počítače v místní síti…");
        ObjC.SetBool(_search, "setEnabled:", false);
        if (_discovered.Count == 0) RebuildList();
        try
        {
            var found = await Task.Run(() => Discovery.FindAsync(TimeSpan.FromSeconds(1.6)));
            MenuBarApp.RunOnMain(() =>
            {
                if (_closed) return;
                _discovered.Clear();
                foreach (var d in found)
                {
                    _discovered[d.Address] = d;
                    if (d.MacAddresses.Length > 0) _settings.WakeMacs[d.Address] = d.MacAddresses.ToList();
                }
                _settings.Save();
                _searching = false;
                RebuildList();
                SetStatus(found.Count switch
                {
                    0 => "Žádný počítač nenalezen. Použijte Přidat pomocí IP – adresu najdete v menu RemoteScreen na druhém počítači.",
                    1 => "Nalezen 1 počítač.",
                    < 5 => $"Nalezeny {found.Count} počítače.",
                    _ => $"Nalezeno {found.Count} počítačů.",
                });
            });
        }
        catch (Exception ex)
        {
            MenuBarApp.RunOnMain(() => { _searching = false; RebuildList(); SetStatus("Vyhledávání selhalo: " + ex.Message, error: true); });
        }
        finally
        {
            MenuBarApp.RunOnMain(() => { _searching = false; if (!_closed && !_busy) ObjC.SetBool(_search, "setEnabled:", true); });
        }
    }

    private void AddByIp()
    {
        string? input = UI.Prompt("Přidat pomocí IP",
            "Zadejte IP adresu nebo název počítače (případně s portem, např. 192.168.1.20:47800).\n" +
            "Adresu najdete v menu RemoteScreen na druhém počítači.", "", "192.168.1.20");
        if (string.IsNullOrWhiteSpace(input)) return;
        string address = input.Trim();
        int port = ProtocolInfo.DefaultPort;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.Count(c => c == ':') == 1 && int.TryParse(address[(colon + 1)..], out var p))
        {
            port = p;
            address = address[..colon];
        }
        if (!_settings.Hosts.Any(h => h.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
            _settings.Hosts.Add(new SavedHost(address, port, address, null));
        _settings.Save();
        _selected = address;
        RebuildList();
        ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _password);
        SetStatus("Zadejte heslo a klikněte na Připojit.");
    }

    // ---------------- připojení ----------------

    private async Task ConnectAsync()
    {
        if (_busy) return;
        var entry = Selected();
        if (entry == null)
        {
            SetStatus("Vyberte počítač ze seznamu nebo ho přidejte pomocí IP.", error: true);
            return;
        }
        string password = UI.GetText(_password);
        if (password.Length == 0)
        {
            SetStatus("Zadejte heslo.", error: true);
            ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _password);
            return;
        }

        SetBusy(true);
        _connectCts = new CancellationTokenSource();
        var ct = _connectCts.Token;
        string label = entry.Name.Length > 0 ? entry.Name : entry.Address;
        bool woke = false;

        for (int attempt = 0; ; attempt++)
        {
            SetStatus(woke ? $"Čekám, až se {label} probudí… ({attempt})" : $"Připojuji se k {label}…");
            var client = new RemoteClient();
            try
            {
                await Task.Run(() => client.ConnectAsync(entry.Address, entry.Port, password,
                    (fp, known) => MenuBarApp.RunOnMainSync(() => AskTrust(entry, fp, known)),
                    host => _settings.KnownHosts.TryGetValue(host, out var f) ? f : null, ct), ct);

                MenuBarApp.RunOnMain(() => OnConnected(entry, client));
                return;
            }
            catch (Exception ex)
            {
                client.Dispose();
                if (ct.IsCancellationRequested || _closed) return;

                bool unreachable = ex is SocketException or TimeoutException ||
                                   ex.InnerException is SocketException;
                bool canWake = _settings.WakeMacs.TryGetValue(entry.Address, out var macs) && macs.Count > 0;

                if (unreachable && canWake && !woke)
                {
                    bool wake = MenuBarApp.RunOnMainSync(() => UI.Alert($"{label} neodpovídá",
                        "Počítač je možná uspaný. Mám ho probudit přes síť (Wake-on-LAN) a připojit se, až naběhne?",
                        new[] { "Probudit", "Zrušit" }) == 0);
                    if (!wake) { SetStatus($"{label} neodpovídá."); break; }
                    woke = true;
                    SetStatus($"Probouzím {label}…");
                    try { await WakeOnLan.SendAsync(macs!, entry.Address); }
                    catch (Exception wex) { SetStatus("Probuzení selhalo: " + wex.Message, error: true); break; }
                    await Task.Delay(4000, ct).ContinueWith(_ => { });
                    continue;
                }
                if (unreachable && woke && attempt < 10)
                {
                    try { await WakeOnLan.SendAsync(macs!, entry.Address); } catch { }
                    await Task.Delay(3000, ct).ContinueWith(_ => { });
                    continue;
                }

                string msg = ex switch
                {
                    AuthenticationException ae => ae.Message.Contains("Certifikát") ? ae.Message : "Přihlášení selhalo: " + ae.Message,
                    SocketException se => $"Nelze se připojit ({se.SocketErrorCode}). Běží na druhém počítači RemoteScreen se zapnutým ovládáním?",
                    TimeoutException te => te.Message + " Je počítač zapnutý a ve stejné síti?",
                    _ => "Připojení selhalo: " + ex.Message,
                };
                SetStatus(msg, error: true);
                break;
            }
        }
        SetBusy(false);
    }

    private bool AskTrust(Entry entry, string fingerprint, string? known)
    {
        bool matchesDiscovery = _discovered.TryGetValue(entry.Address, out var d) &&
                                string.Equals(d.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase);
        string shortFp = fingerprint.Length > 47 ? fingerprint[..47] + "…" : fingerprint;
        if (known != null)
        {
            return UI.Alert("Pozor – certifikát počítače se změnil",
                $"Počítač {entry.Address} se prokázal jiným certifikátem než minule.\n\n" +
                "To se stane po přeinstalaci RemoteScreen nebo smazání jeho nastavení. Jinak to může znamenat, " +
                "že se za počítač někdo vydává.\n\n" +
                $"Nový otisk:\n{shortFp}\n\n" +
                "Otisk najdete na druhém počítači v menu RemoteScreen → Kopírovat otisk certifikátu.",
                new[] { "Zrušit", "Důvěřovat novému" }, warning: true) == 1;
        }
        return UI.Alert($"Připojit k {entry.Name}?",
            "Připojujete se k tomuto počítači poprvé. Zkontrolujte, že otisk certifikátu odpovídá tomu " +
            "v menu RemoteScreen na druhém počítači.\n\n" +
            $"Otisk:\n{shortFp}\n\n" +
            (matchesDiscovery ? "(Shoduje se s otiskem z vyhledávání v síti.)" : ""),
            new[] { "Důvěřovat a připojit", "Zrušit" }) == 0;
    }

    private void OnConnected(Entry entry, RemoteClient client)
    {
        var info = client.Info;
        _settings.KnownHosts[entry.Address] = client.Fingerprint;
        if (info.MacAddresses is { Length: > 0 } macs) _settings.WakeMacs[entry.Address] = macs.ToList();
        string name = info.HostName ?? entry.Name;
        _settings.Remember(new SavedHost(entry.Address, entry.Port, name, info.Platform ?? "macos"));
        _settings.Save();

        Log.Ok($"Připojeno k {name} ({entry.Address}, {info.Platform ?? "macos"})");
        SetBusy(false);
        ObjC.Set(_password, "setStringValue:", ObjC.Str(""));
        RemoteWindow.Start(client, name, _settings);
        ObjC.Send(_window, ObjC.Sel("close"));
    }

    // ---------------- stav ----------------

    private void SetBusy(bool busy)
    {
        MenuBarApp.RunOnMain(() =>
        {
            _busy = busy;
            if (_closed) return;
            ObjC.SetBool(_connect, "setEnabled:", !busy);
            ObjC.SetBool(_search, "setEnabled:", !busy && !_searching);
            ObjC.SetBool(_addIp, "setEnabled:", !busy);
            ObjC.Set(_connect, "setTitle:", ObjC.Str(busy ? "Připojuji…" : "Připojit"));
        });
    }

    private void SetStatus(string text, bool error = false)
    {
        MenuBarApp.RunOnMain(() =>
        {
            if (_closed) return;
            UI.SetText(_status, text);
            ObjC.Set(_status, "setTextColor:", error ? UI.Danger : UI.TextDim);
        });
    }

    public void WillClose()
    {
        _closed = true;
        _connectCts?.Cancel();
        foreach (var c in _rowControls) UI.ForgetAction(c);
        foreach (var c in new[] { _connect, _search, _addIp }) UI.ForgetAction(c);
        if (_instance == this) _instance = null;
        IntPtr w = _window;
        UI.After(500, () => ObjC.Send(w, ObjC.Sel("release")));
    }

    // ---------------- třída s převráceným souřadným systémem (rozložení shora dolů) ----------------

    private static IntPtr _flippedClass;

    internal static unsafe IntPtr FlippedViewClass()
    {
        if (_flippedClass != IntPtr.Zero) return _flippedClass;
        IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSView"), "RSFlippedView", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("isFlipped"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&IsFlipped, "c@:");
        ObjC.objc_registerClassPair(cls);
        return _flippedClass = cls;
    }

    [UnmanagedCallersOnly]
    private static byte IsFlipped(IntPtr self, IntPtr sel) => 1;
}
