using System.Diagnostics;
using RemoteScreen.Shared;

namespace RemoteScreen.Host;

/// <summary>Tvar kurzoru: PNG, hotspot a velikost ve stejných jednotkách jako DisplayInfo (Mac: body, Windows: pixely).</summary>
public sealed record CursorShapeData(byte[] Png, float HotX, float HotY, float Width, float Height);

/// <summary>Vkládání myši a klávesnice. Klávesy přicházejí jako kódy macOS (kVK_*) – Windows je převádí.</summary>
public interface IInputSink
{
    /// <summary>Displej, ke kterému se vztahují normalizované souřadnice 0..1.</summary>
    DisplayInfo Display { get; set; }
    void MouseMove(float nx, float ny);
    void MouseButton(byte button, bool down, int clicks, float nx, float ny);
    /// <summary>Delta ve stylu Windows: 120 = jeden zub kolečka.</summary>
    void Scroll(short dy, short dx);
    void Key(ushort macKeyCode, bool down);
    void TypeText(string text);
    void ReleaseAll();
}

/// <summary>Vše, co se liší mezi macOS a Windows.</summary>
public interface IHostPlatform
{
    /// <summary>"macos" nebo "windows" – klient podle toho volí převod kláves.</summary>
    string Name { get; }
    string OsVersion { get; }
    string HomeDir { get; }

    DisplayInfo[] GetDisplays();
    byte[]? CaptureJpeg(DisplayInfo display, int maxWidth, int quality);

    /// <summary>Nasnímá displej do surových pixelů BGRX (32 bitů, řádky shora dolů), zmenšené na nejvýš maxWidth.
    /// Vyrovnávací paměť <paramref name="frame"/> se použije znovu, pokud sedí rozměry. false = nejde (pak se použije CaptureJpeg).</summary>
    bool CaptureRaw(DisplayInfo display, int maxWidth, ref RawFrame? frame) => false;

    /// <summary>Zakóduje obdélník ze surového snímku do JPEG.</summary>
    byte[]? EncodeRegion(RawFrame frame, int x, int y, int w, int h, int quality) => null;
    /// <summary>Nový tvar kurzoru, nebo null, pokud se od posledně nezměnil.</summary>
    CursorShapeData? GetCursorIfChanged(ref int lastHash);
    IInputSink CreateInput(DisplayInfo initial);

    void WakeDisplay(bool force);
    void SetKeepAwake(bool on);

    Task<ActionResult> RunActionAsync(ActionRequest req, IInputSink input);
    Task<string> GetClipboardAsync();
    Task SetClipboardAsync(string text);

    /// <summary>Číslo, které se mění s každou změnou schránky (rychlé zjištění bez čtení obsahu). -1 = neumí.</summary>
    long ClipboardChangeCount() => -1;
    Task<(bool Ok, string Message)> MoveToTrashAsync(string path);
    /// <summary>Proces pro příkaz z terminálu (přesměrování výstupu nastaví volající).</summary>
    ProcessStartInfo CreateShell(string command, string workingDirectory);

    /// <summary>Kam uložit soubory puštěné v bodě (x, y) v souřadnicích displejů; null = neznámý bod.</summary>
    Task<DropTarget> ResolveDropTargetAsync(double? x, double? y);
    /// <summary>Tažení souborů z tohoto počítače ven: počítadlo změn (-1 = nepodporováno) a tažené cesty.</summary>
    long DragChangeCount();
    List<string> DragFilePaths();

    /// <summary>Srozumitelná hláška pro chybu přístupu k souboru/složce.</summary>
    string AccessDeniedMessage { get; }
}

/// <summary>Surový snímek obrazovky: BGRX, 4 bajty na pixel, řádky shora dolů.</summary>
public sealed class RawFrame
{
    public RawFrame(int width, int height)
    {
        Width = width;
        Height = height;
        Stride = width * 4;
        Pixels = new byte[Stride * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }

    public bool Matches(int width, int height) => Width == width && Height == height;
}
