using System.Text.Json;
using RemoteScreen.Shared;

namespace RemoteScreen.Client;

/// <summary>Nastavení v %APPDATA%\RemoteScreen\settings.json (heslo se neukládá).</summary>
public sealed class ClientSettings
{
    public List<string> RecentHosts { get; set; } = new();
    /// <summary>Boční menu v okně plochy: vysunuté a svislá poloha šipky (0–1).</summary>
    public bool DockOpen { get; set; } = true;
    /// <summary>Automaticky sdílet schránku s ovládaným počítačem (text, oběma směry).</summary>
    public bool ClipboardSync { get; set; } = true;
    /// <summary>Barva zvýraznění (#RRGGBB).</summary>
    public string AccentColor { get; set; } = "#FF8A3D";
    public float DockPosition { get; set; } = 0.5f;
    /// <summary>Poloha stavové bubliny (0–1); záporné = výchozí vpravo nahoře.</summary>
    public float StatusX { get; set; } = -1;
    public float StatusY { get; set; } = -1;
    /// <summary>adresa → název a systém počítače (pro dlaždice v přihlášení)</summary>
    public Dictionary<string, HostInfo> Hosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Port { get; set; } = ProtocolInfo.DefaultPort;
    /// <summary>host → SHA-256 otisk certifikátu (trust on first use)</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>host → MAC adresy Macu pro probuzení (Wake-on-LAN)</summary>
    public Dictionary<string, List<string>> WakeMacs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int QualityIndex { get; set; } = 1;
    /// <summary>index v nabídce 15/30/60/90/120/144</summary>
    public int FpsIndex { get; set; } = 2;
    public bool SwapCtrlCmd { get; set; } = true;
    public bool CaptureKeyboard { get; set; } = true;
    /// <summary>Ovládání tohoto PC bylo naposledy zapnuté (obnoví se při spuštění aplikace).</summary>
    public bool SharingEnabled { get; set; }

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteScreen");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static ClientSettings Load()
    {
        // převzít nastavení z předchozího názvu aplikace (MacRemote)
        try
        {
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacRemote", "settings.json");
            if (!File.Exists(FilePath) && File.Exists(old))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(old, FilePath);
            }
        }
        catch { }

        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath));
                if (s != null)
                {
                    s.KnownHosts = new Dictionary<string, string>(s.KnownHosts, StringComparer.OrdinalIgnoreCase);
                    s.WakeMacs = new Dictionary<string, List<string>>(s.WakeMacs ?? new(), StringComparer.OrdinalIgnoreCase);
                    s.Hosts = new Dictionary<string, HostInfo>(s.Hosts ?? new(), StringComparer.OrdinalIgnoreCase);
                    return s;
                }
            }
        }
        catch { }
        return new ClientSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void ForgetHost(string host)
    {
        RecentHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        Hosts.Remove(host);
        KnownHosts.Remove(host);
        WakeMacs.Remove(host);
    }

    public void RememberHost(string host)
    {
        RecentHosts.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        RecentHosts.Insert(0, host);
        if (RecentHosts.Count > 10) RecentHosts.RemoveRange(10, RecentHosts.Count - 10);
    }
}

/// <summary>Uložené údaje o počítači.</summary>
public sealed record HostInfo(string Name, string? Platform, int Port);
