using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemoteScreen.Shared;

/// <summary>Počítač s RemoteScreen nalezený v místní síti.</summary>
public sealed record DiscoveredHost(string Name, string Address, int Port, string Fingerprint, string[] MacAddresses, string? Platform)
{
    public bool IsWindows => Platform == "windows";
    public string PlatformLabel => IsWindows ? "Windows" : "macOS";
}

/// <summary>Hledání počítačů s RemoteScreen v místní síti (UDP broadcast).</summary>
public static class Discovery
{
    public static async Task<List<DiscoveredHost>> FindAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var found = new Dictionary<string, DiscoveredHost>();
        var own = LocalIPv4();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var req = Encoding.ASCII.GetBytes(ProtocolInfo.DiscoveryRequest);

        // dvě kola – první paket se v některých sítích ztratí
        for (int round = 0; round < 2; round++)
        {
            foreach (var bc in WakeOnLan.BroadcastAddresses())
            {
                try { await udp.SendAsync(req, req.Length, new IPEndPoint(bc, ProtocolInfo.DiscoveryPort)); }
                catch { /* rozhraní bez broadcastu */ }
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(round == 0 ? wait / 2 : wait / 2);
            try
            {
                while (true)
                {
                    var r = await udp.ReceiveAsync(cts.Token);
                    try
                    {
                        var reply = JsonSerializer.Deserialize<DiscoveryReply>(r.Buffer, ProtocolInfo.Json);
                        if (reply == null) continue;
                        string addr = r.RemoteEndPoint.Address.ToString();
                        if (own.Contains(addr)) continue; // tento počítač (zapnuté sdílení) nenabízet
                        found[addr] = new DiscoveredHost(reply.Name, addr, reply.Port, reply.Fingerprint,
                            reply.MacAddresses ?? Array.Empty<string>(), reply.Platform);
                    }
                    catch (JsonException) { }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        return found.Values.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static HashSet<string> LocalIPv4()
    {
        var set = new HashSet<string> { "127.0.0.1" };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                    set.Add(ua.Address.ToString());
        }
        catch { }
        return set;
    }
}
