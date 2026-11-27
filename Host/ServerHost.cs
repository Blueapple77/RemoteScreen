using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RemoteScreen.Shared;

namespace RemoteScreen.Host;

public sealed record SessionInfo(string ClientName, string Address, DateTime Since, bool Streaming);

/// <summary>Jádro serveru: naslouchání, vyhledávání v síti, evidence připojených klientů.
/// Lze ho za běhu zastavit a znovu spustit (menu „Restartovat server“).</summary>
public sealed class ServerHost
{
    public IHostPlatform Platform { get; }
    private bool _keepAwake;

    public ServerHost(IHostPlatform platform) => Platform = platform;

    private readonly object _gate = new();
    private readonly List<ClientSession> _sessions = new();
    private TcpListener? _listener;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public ServerConfig Config { get; private set; } = ServerConfig.Load();
    public X509Certificate2? Certificate { get; private set; }
    public string Fingerprint { get; private set; } = "";
    public bool Running { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Změna stavu (start/stop, připojení/odpojení klienta). Volá se z libovolného vlákna.</summary>
    public event Action? StateChanged;

    public void Start()
    {
        lock (_gate)
        {
            if (Running) return;
            Config = ServerConfig.Load();
            if (!Config.HasPassword)
            {
                LastError = "Není nastavené heslo";
                Log.Warn(LastError);
            }
            else
            {
                try
                {
                    Certificate ??= ServerConfig.LoadOrCreateCertificate();
                    Fingerprint = AuthCrypto.Fingerprint(Certificate.RawData);

                    _listener = new TcpListener(IPAddress.Any, Config.Port);
                    _listener.Start();
                    _cts = new CancellationTokenSource();
                    var ct = _cts.Token;
                    var listener = _listener;
                    _ = Task.Run(() => AcceptLoopAsync(listener, ct));
                    _ = Task.Run(() => DiscoveryLoopAsync(ct));

                    Running = true;
                    LastError = null;
                    Log.Ok($"Server „{Config.Name}“ naslouchá na portu {Config.Port}");
                    foreach (var ip in LocalAddresses()) Log.Info("  adresa: " + ip);
                    Log.Info("Otisk certifikátu: " + Fingerprint);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    LastError = $"Port {Config.Port} je obsazený (neběží RemoteScreen už jinde?)";
                    Log.Error(LastError);
                    _listener = null;
                }
                catch (Exception ex)
                {
                    LastError = "Server nelze spustit: " + ex.Message;
                    Log.Error(LastError);
                    _listener = null;
                }
            }
        }
        NotifyChanged();
    }

    public void Stop()
    {
        ClientSession[] sessions;
        lock (_gate)
        {
            if (!Running) return;
            Running = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _udp?.Dispose(); } catch { }
            _listener = null;
            _udp = null;
            sessions = _sessions.ToArray();
        }
        foreach (var s in sessions) s.Disconnect();
        Log.Info("Server zastaven.");
        NotifyChanged();
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public void DisconnectAll()
    {
        ClientSession[] sessions;
        lock (_gate) sessions = _sessions.ToArray();
        foreach (var s in sessions) s.Disconnect();
    }

    public SessionInfo[] Sessions
    {
        get
        {
            lock (_gate)
                return _sessions.Where(s => s.Authenticated)
                    .Select(s => new SessionInfo(s.ClientName, s.Address, s.Since, s.Streaming))
                    .ToArray();
        }
    }

    internal void Register(ClientSession s)
    {
        lock (_gate) _sessions.Add(s);
        lock (Controllers) Controllers.Add(s.Address);
    }

    internal void Unregister(ClientSession s)
    {
        lock (_gate) _sessions.Remove(s);
        lock (Controllers) Controllers.Remove(s.Address);
        NotifyChanged();
    }

    // adresy počítačů, které tento počítač právě ovládají (může jich být víc i ze stejné adresy)
    private static readonly List<string> Controllers = new();

    /// <summary>Ovládá tento počítač právě počítač s danou IP adresou? Pak by přeposílání
    /// vloženého vstupu zpět na něj vytvořilo smyčku (PC ovládá Mac, který ovládá to PC).</summary>
    public static bool IsControlledFrom(string? address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        lock (Controllers) return Controllers.Contains(address);
    }

    internal void NotifyChanged()
    {
        UpdateKeepAwake();
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Log.Warn("StateChanged: " + ex.Message); }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcp = await listener.AcceptTcpClientAsync(ct);
                var session = new ClientSession(tcp, Certificate!, Config, this);
                Register(session);
                _ = Task.Run(session.RunAsync);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn("Přijetí spojení selhalo: " + ex.Message);
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        try
        {
            var udp = new UdpClient(new IPEndPoint(IPAddress.Any, ProtocolInfo.DiscoveryPort));
            lock (_gate) _udp = udp;
            var reply = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply(Config.Name, Config.Port, Fingerprint, MacAddresses(), Platform.Name), ProtocolInfo.Json);
            while (!ct.IsCancellationRequested)
            {
                var r = await udp.ReceiveAsync(ct);
                if (Encoding.ASCII.GetString(r.Buffer) == ProtocolInfo.DiscoveryRequest)
                    await udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                Log.Warn("Automatické vyhledávání v síti nefunguje: " + ex.Message);
        }
    }

    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            string mac = FormatMac(ni.GetPhysicalAddress().GetAddressBytes());
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    list.Add(mac.Length > 0 ? $"{ua.Address}  ({ni.Name}, MAC {mac})" : $"{ua.Address}  ({ni.Name})");
        }
        return list;
    }

    /// <summary>MAC adresy karet, které mají IPv4 adresu (en0 = obvykle kabel, en1 = Wi-Fi).</summary>
    public static string[] MacAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(ni => ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderBy(ni => ni.Name, StringComparer.Ordinal)
                .Select(ni => FormatMac(ni.GetPhysicalAddress().GetAddressBytes()))
                .Where(m => m.Length > 0)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string FormatMac(byte[] b) =>
        b.Length == 6 && b.Any(x => x != 0) ? string.Join(':', b.Select(x => x.ToString("X2"))) : "";

    /// <summary>Dokud je někdo připojený, počítač neusne. Po odpojení se vše vrátí do normálu.</summary>
    private void UpdateKeepAwake()
    {
        bool any;
        lock (_gate) any = _sessions.Any(s => s.Authenticated);
        if (any == _keepAwake) return;
        _keepAwake = any;
        try
        {
            Platform.SetKeepAwake(any);
            Log.Info(any ? "Klient připojen – počítač nebude usínat, dokud ho někdo ovládá." : "Nikdo není připojen – počítač může zase usínat.");
        }
        catch (Exception ex)
        {
            Log.Warn("Zabránění spánku: " + ex.Message);
        }
    }
}
