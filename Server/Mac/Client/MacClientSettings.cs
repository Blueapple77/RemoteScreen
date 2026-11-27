using System.Text.Json;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Počítač, ke kterému se tento Mac už připojil.</summary>
internal sealed record SavedHost(string Address, int Port, string Name, string? Platform);

/// <summary>Nastavení ovládání jiných počítačů z tohoto Macu
/// (~/Library/Application Support/RemoteScreen/client.json). Heslo se neukládá.</summary>
internal sealed class MacClientSettings
{
    public List<SavedHost> Hosts { get; set; } = new();
    /// <summary>adresa → SHA-256 otisk certifikátu (důvěra při prvním připojení)</summary>
    public Dictionary<string, string> KnownHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>adresa → MAC adresy pro probuzení (Wake-on-LAN)</summary>
    public Dictionary<string, List<string>> WakeMacs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Fps { get; set; } = 60;
    public int Quality { get; set; } = 80;
    public int MaxWidth { get; set; } = 2560;
    /// <summary>Při ovládání Windows: Cmd funguje jako Ctrl (Cmd+C = kopírovat).</summary>
    public bool SwapCmdCtrl { get; set; } = true;
    public string? LastHost { get; set; }
    /// <summary>Boční menu v okně plochy: vysunuté a svislá poloha šipky (0 = nahoře, 1 = dole).</summary>
    public bool DockOpen { get; set; } = true;
    /// <summary>Automaticky sdílet schránku s ovládaným počítačem (text, oběma směry).</summary>
    public bool ClipboardSync { get; set; } = true;
    /// <summary>Barva zvýraznění (#RRGGBB).</summary>
    public string Accent { get; set; } = "#FF8A3D";
    public double DockPosition { get; set; } = 0.5;
    /// <summary>Poloha stavové bubliny (levý horní roh, 0–1); záporné = výchozí vpravo nahoře.</summary>
    public double PillX { get; set; } = -1;
    public double PillY { get; set; } = -1;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "RemoteScreen");
    private static string FilePath => Path.Combine(Dir, "client.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static MacClientSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<MacClientSettings>(File.ReadAllText(FilePath), Options);
                if (s != null)
                {
                    s.KnownHosts = new Dictionary<string, string>(s.KnownHosts ?? new(), StringComparer.OrdinalIgnoreCase);
                    s.WakeMacs = new Dictionary<string, List<string>>(s.WakeMacs ?? new(), StringComparer.OrdinalIgnoreCase);
                    s.Hosts ??= new();
                    return s;
                }
            }
        }
        catch (Exception ex) { Log.Warn("Nastavení klienta nelze načíst: " + ex.Message); }
        return new MacClientSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) { Log.Warn("Nastavení klienta nelze uložit: " + ex.Message); }
    }

    public void Remember(SavedHost host)
    {
        Hosts.RemoveAll(h => string.Equals(h.Address, host.Address, StringComparison.OrdinalIgnoreCase));
        Hosts.Insert(0, host);
        if (Hosts.Count > 12) Hosts.RemoveRange(12, Hosts.Count - 12);
        LastHost = host.Address;
    }

    public void Forget(string address)
    {
        Hosts.RemoveAll(h => string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase));
        KnownHosts.Remove(address);
        WakeMacs.Remove(address);
    }
}
