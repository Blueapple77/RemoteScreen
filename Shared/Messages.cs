namespace RemoteScreen.Shared;

// --- autentizace ---
public record AuthChallenge(int Version, string Salt, string Nonce, int Iterations, string ServerName);
public record AuthResponse(string Proof, string ClientName);

public record DisplayInfo(
    int Index, uint Id,
    double X, double Y, double Width, double Height,
    bool IsMain);

public record AuthResult(
    bool Ok, string Message,
    string? HostName = null, string? OsVersion = null, string? HomeDir = null,
    DisplayInfo[]? Displays = null, string[]? MacAddresses = null,
    /// <summary>"macos" nebo "windows" (null = starší server = macOS)</summary>
    string? Platform = null);

// --- obraz ---
/// <param name="MaxWidth">0 = plné rozlišení</param>
/// <param name="Delta">Klient umí <see cref="MsgType.FramePatch"/> (posílat jen změněné části obrazu).</param>
public record StreamStart(int DisplayIndex, int Fps, int Quality, int MaxWidth, bool Delta = false);

// --- příkazy ---
public record ExecRequest(int Id, string Command);
public record ExecOutput(int Id, string Text, bool IsError);
public record ExecExit(int Id, int ExitCode, string Cwd);
public record ExecKill(int Id);

// --- soubory ---
public record PathRequest(string Path);
public record DirEntry(string Name, bool IsDir, long Size, DateTime Modified);
public record DirListing(string Path, bool Ok, string? Error, DirEntry[] Entries);
public record DownloadRequest(int TransferId, string Path);
public record FileStart(int TransferId, string Name, long Size);
public record FileEnd(int TransferId, bool Ok, string? Error);
/// <param name="Overwrite">false = pokud soubor existuje, uloží se jako „název 2.ext“</param>
/// <param name="Root">Kořen nahrávané složky (Path leží uvnitř). Bez přepisování ho server v rámci dávky
/// přejmenuje celý („Foo 2“), místo aby přejmenovával jednotlivé soubory v existující složce.</param>
/// <param name="Batch">Číslo dávky nahrávání (soubory jedné akce).</param>
public record UploadStart(int TransferId, string Path, long Size, bool Overwrite, string? Root = null, int Batch = 0);
/// <summary>Místo puštění souborů v obrazu Macu (0..1); záporné = neznámé.</summary>
public record DropQuery(float X, float Y);
/// <summary>Kam se uloží soubory přetažené do okna plochy.</summary>
/// <param name="OpenWithApp">cesta k aplikaci (.app), ve které se mají soubory po nahrání otevřít</param>
public record DropTarget(string Path, string Label, string? OpenWithApp = null);

// --- tažení souborů z Macu do Windows ---
public record DragItem(string Path, string Relative, long Size);
/// <param name="Roots">názvy tažených položek (soubory i složky) v nejvyšší úrovni</param>
public record DragFiles(DragItem[] Items, string[] Roots);
public record UploadEnd(int TransferId);
/// <param name="TransferId">U nahrávání: ke kterému souboru výsledek patří (Path = skutečná cesta po přejmenování).</param>
public record FileOpResult(string Op, string Path, bool Ok, string? Message, int TransferId = 0);

// --- systém ---
public record ActionRequest(string Name, string? Arg = null);
public record ActionResult(string Name, bool Ok, string Output);
/// <param name="Auto">Automatické sdílení schránky (bez hlášení a potvrzování).</param>
public record ClipboardText(string Text, bool Auto = false)
{
    /// <summary>Porovnání bez ohledu na konce řádků (Windows \r\n × macOS \n).</summary>
    public static bool Same(string? a, string? b) =>
        string.Equals(a?.Replace("\r\n", "\n"), b?.Replace("\r\n", "\n"), StringComparison.Ordinal);

    /// <summary>Nejvíc 1 MB textu – větší schránka se automaticky nesdílí.</summary>
    public const int MaxAutoLength = 1_000_000;
}
public record ErrorInfo(string Message);

// --- vyhledávání v síti ---
/// <param name="MacAddresses">MAC adresy síťových karet Macu – pro probuzení (Wake-on-LAN)</param>
public record DiscoveryReply(string Name, int Port, string Fingerprint, string[]? MacAddresses = null, string? Platform = null);
