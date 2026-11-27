using System.Diagnostics;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Sdílení Macu: napojení společného jádra na macOS (CoreGraphics, AppKit, AppleScript).</summary>
internal sealed class MacPlatform : IHostPlatform
{
    private Process? _caffeinate;
    private readonly object _gate = new();

    public string Name => "macos";
    public string OsVersion { get; }
    public string HomeDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public MacPlatform()
    {
        string v = "";
        try { v = SystemActions.RunProcessAsync("/usr/bin/sw_vers", new[] { "-productVersion" }).GetAwaiter().GetResult().Output.Trim(); }
        catch { }
        OsVersion = "macOS " + v;
    }

    public DisplayInfo[] GetDisplays() => ScreenCapture.GetDisplays();
    public byte[]? CaptureJpeg(DisplayInfo display, int maxWidth, int quality) => ScreenCapture.CaptureJpeg(display, maxWidth, quality);
    public bool CaptureRaw(DisplayInfo display, int maxWidth, ref RawFrame? frame) => ScreenCapture.CaptureRaw(display, maxWidth, ref frame);
    public byte[]? EncodeRegion(RawFrame frame, int x, int y, int w, int h, int quality) => ScreenCapture.EncodeRegion(frame, x, y, w, h, quality);
    public CursorShapeData? GetCursorIfChanged(ref int lastHash) => CursorCapture.GetIfChanged(ref lastHash);
    public IInputSink CreateInput(DisplayInfo initial)
    {
        WindowLookup.Warmup(); // nový klient → připravit Finder na první puštění souborů
        return new InputInjector(initial);
    }

    public void WakeDisplay(bool force) => DisplayWake.Wake(force);

    public void SetKeepAwake(bool on)
    {
        lock (_gate)
        {
            if (on && _caffeinate == null)
            {
                var psi = new ProcessStartInfo("/usr/bin/caffeinate") { UseShellExecute = false };
                foreach (var a in new[] { "-d", "-i", "-w", Environment.ProcessId.ToString() }) psi.ArgumentList.Add(a);
                _caffeinate = Process.Start(psi);
            }
            else if (!on && _caffeinate != null)
            {
                try { if (!_caffeinate.HasExited) _caffeinate.Kill(); } catch { }
                _caffeinate.Dispose();
                _caffeinate = null;
            }
        }
    }

    public Task<ActionResult> RunActionAsync(ActionRequest req, IInputSink input) =>
        SystemActions.RunAsync(req, (InputInjector)input);

    public Task<string> GetClipboardAsync() => SystemActions.GetClipboardAsync();
    public Task SetClipboardAsync(string text) => SystemActions.SetClipboardAsync(text.Replace("\r\n", "\n"));

    private static bool _appKit;

    /// <summary>NSPasteboard.changeCount – mění se s každou změnou schránky.</summary>
    public long ClipboardChangeCount()
    {
        try
        {
            if (!_appKit) { ObjC.LoadAppKit(); _appKit = true; }
            IntPtr pb = ObjC.Get(ObjC.Cls("NSPasteboard"), "generalPasteboard");
            return pb == IntPtr.Zero ? -1 : ObjC.SendRetInt(pb, ObjC.Sel("changeCount"));
        }
        catch { return -1; }
    }

    public async Task<(bool Ok, string Message)> MoveToTrashAsync(string path)
    {
        var r = await SystemActions.RunProcessAsync("/usr/bin/osascript", new[]
        {
            "-e", $"tell application \"Finder\" to delete (POSIX file \"{SystemActions.Esc(path)}\" as alias)",
        });
        return (r.Code == 0, r.Code == 0 ? "Přesunuto do koše" : r.Output.Trim());
    }

    public ProcessStartInfo CreateShell(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo("/bin/zsh");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
        psi.Environment["TERM"] = "dumb";
        if (!psi.Environment.ContainsKey("LANG")) psi.Environment["LANG"] = "en_US.UTF-8";
        return psi;
    }

    public Task<DropTarget> ResolveDropTargetAsync(double? x, double? y) => WindowLookup.ResolveAsync(x, y);
    public long DragChangeCount() => DragPasteboard.ChangeCount();
    public List<string> DragFilePaths() => DragPasteboard.FilePaths();

    public string AccessDeniedMessage =>
        "macOS nepovolil přístup. Na Macu potvrďte dotaz na přístup ke složce, nebo povolte RemoteScreen " +
        "v Nastavení → Soukromí a zabezpečení → Plný přístup k disku (odkaz je v menu RemoteScreen v horní liště).";
}
