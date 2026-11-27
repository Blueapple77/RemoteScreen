using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteScreen.Shared;

/// <summary>Probuzení Macu ze spánku „magickým paketem“ (Wake-on-LAN).</summary>
public static class WakeOnLan
{
    public static bool TryParseMac(string text, out byte[] mac)
    {
        string hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        mac = Array.Empty<byte>();
        if (hex.Length != 12) return false;
        mac = Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
        return true;
    }

    public static string Format(byte[] mac) => string.Join(':', mac.Select(b => b.ToString("X2")));

    /// <summary>Pošle magický paket (6× FF + 16× MAC) na všechny broadcastové adresy i přímo na Mac, porty 9 a 7.</summary>
    public static async Task SendAsync(IEnumerable<string> macs, string? host)
    {
        var packets = new List<byte[]>();
        foreach (var m in macs)
        {
            if (!TryParseMac(m, out var mac)) continue;
            var p = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) p[i] = 0xFF;
            for (int i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, p, 6 + i * 6, 6);
            packets.Add(p);
        }
        if (packets.Count == 0) throw new ArgumentException("Neplatná MAC adresa.");

        var targets = BroadcastAddresses().ToList();
        if (host != null && IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            targets.Add(ip);

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        for (int round = 0; round < 3; round++)
        {
            foreach (var packet in packets)
            foreach (var target in targets)
            foreach (int port in new[] { 9, 7 })
            {
                try { await udp.SendAsync(packet, packet.Length, new IPEndPoint(target, port)); }
                catch { /* rozhraní bez broadcastu */ }
            }
            await Task.Delay(250);
        }
    }

    /// <summary>255.255.255.255 + broadcast každé aktivní IPv4 sítě.</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 0)) continue;
                var bc = new byte[4];
                for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bc);
            }
        }
    }
}
