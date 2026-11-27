using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using RemoteScreen.Host;
using RemoteScreen.Shared;
using static RemoteScreen.Client.Host.WinNative;

namespace RemoteScreen.Client.Host;

/// <summary>Sdílení tohoto PC: napojení společného jádra na Windows.</summary>
internal sealed class WinPlatform : IHostPlatform
{
    private readonly Control _ui;               // pro volání, která musí běžet na UI vlákně (schránka, koš, napájení)
    private readonly Action<string> _notify;    // oznámení u ikony na hlavním panelu
    private DateTime _lastWake = DateTime.MinValue;

    public WinPlatform(Control ui, Action<string> notify)
    {
        _ui = ui;
        _notify = notify;
    }

    public string Name => "windows";
    public string OsVersion { get; } = RuntimeInformation.OSDescription.Replace("Microsoft ", "");
    public string HomeDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string AccessDeniedMessage => "Windows nepovolil přístup k této položce (chybí oprávnění).";

    private T OnUi<T>(Func<T> f) => _ui.InvokeRequired ? (T)_ui.Invoke(f) : f();
    private void OnUi(Action a) { if (_ui.InvokeRequired) _ui.Invoke(a); else a(); }

    public DisplayInfo[] GetDisplays() => WinCapture.GetDisplays();
    public byte[]? CaptureJpeg(DisplayInfo display, int maxWidth, int quality) => WinCapture.CaptureJpeg(display, maxWidth, quality);
    public bool CaptureRaw(DisplayInfo display, int maxWidth, ref RawFrame? frame) => WinCapture.CaptureRaw(display, maxWidth, ref frame);
    public byte[]? EncodeRegion(RawFrame frame, int x, int y, int w, int h, int quality) => WinCapture.EncodeRegion(frame, x, y, w, h, quality);
    public CursorShapeData? GetCursorIfChanged(ref int lastHash) => WinCapture.GetCursorIfChanged(ref lastHash);
    public IInputSink CreateInput(DisplayInfo initial) => new WinInput(initial);

    public void WakeDisplay(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastWake).TotalSeconds < 5) return;
        _lastWake = now;
        SetThreadExecutionState(ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);
        if (force) Send(Mouse(1, 0, MOUSEEVENTF_MOVE), Mouse(-1, 0, MOUSEEVENTF_MOVE)); // drobný pohyb rozsvítí displej
    }

    public void SetKeepAwake(bool on) =>
        OnUi(() => SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED : ES_CONTINUOUS));

    // ---------------- akce ----------------

    public async Task<ActionResult> RunActionAsync(ActionRequest req, IInputSink input)
    {
        string arg = req.Arg ?? "";
        var win = (WinInput)input;
        try
        {
            switch (req.Name)
            {
                case "volume_up": return Ok(req, WinAudio.SetVolume(WinAudio.GetVolume() + 6).ToString());
                case "volume_down": return Ok(req, WinAudio.SetVolume(WinAudio.GetVolume() - 6).ToString());
                case "volume_set": return Ok(req, WinAudio.SetVolume(int.TryParse(arg, out var v) ? v : 50).ToString());
                case "volume_get": return Ok(req, WinAudio.GetVolume().ToString());
                case "volume_mute": return Ok(req, WinAudio.ToggleMute() ? "true" : "false");

                case "media_playpause": win.TapVk(0xB3); return Ok(req, "OK");
                case "media_next": win.TapVk(0xB0); return Ok(req, "OK");
                case "media_prev": win.TapVk(0xB1); return Ok(req, "OK");

                case "lock":
                    LockWorkStation();
                    return Ok(req, "Počítač zamčen");
                case "sleep":
                    _ = Task.Run(async () => { await Task.Delay(500); OnUi(() => Application.SetSuspendState(PowerState.Suspend, false, false)); });
                    return Ok(req, "Uspávám");
                case "display_sleep":
                    PostMessage(new IntPtr(0xFFFF), 0x0112 /* WM_SYSCOMMAND */, new IntPtr(0xF170) /* SC_MONITORPOWER */, new IntPtr(2));
                    return Ok(req, "Displej vypnut");
                case "display_wake":
                    WakeDisplay(true);
                    return Ok(req, "Displej rozsvícen");
                case "force_quit":
                    Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
                    return Ok(req, "Otevřen Správce úloh");
                case "mission_control":
                    win.TapVk(0x5B, 0x09); // Win + Tab
                    return Ok(req, "OK");
                case "restart": return await Shutdown(req, "/r");
                case "shutdown": return await Shutdown(req, "/s");
                case "logout": return await Shutdown(req, "/l");

                case "open_app":
                    Process.Start(new ProcessStartInfo(arg) { UseShellExecute = true });
                    return Ok(req, "Spuštěno: " + arg);
                case "quit_app":
                {
                    string name = Path.GetFileNameWithoutExtension(arg);
                    var procs = Process.GetProcessesByName(name);
                    foreach (var p in procs) try { p.CloseMainWindow(); } catch { }
                    return new ActionResult(req.Name, procs.Length > 0, procs.Length > 0 ? "Ukončeno: " + name : "Aplikace neběží: " + name);
                }
                case "list_apps":
                {
                    var names = Process.GetProcesses()
                        .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0; } catch { return false; } })
                        .Select(p => p.ProcessName)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                    return Ok(req, string.Join("\n", names));
                }
                case "open":
                    Process.Start(new ProcessStartInfo(arg) { UseShellExecute = true });
                    return Ok(req, "Otevřeno");
                case "open_with":
                {
                    var lines = arg.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length < 2) return new ActionResult(req.Name, false, "Chybí aplikace nebo soubory");
                    var psi = new ProcessStartInfo(lines[0]) { UseShellExecute = false };
                    foreach (var f in lines.Skip(1)) psi.ArgumentList.Add(f);
                    Process.Start(psi);
                    return Ok(req, "Otevřeno");
                }
                case "notify":
                    _notify(arg);
                    return Ok(req, "OK");
                case "say":
                {
                    string text = arg.Replace("'", "''");
                    var r = await ProcessUtil.RunAsync("powershell.exe", new[]
                    {
                        "-NoProfile", "-NonInteractive", "-Command",
                        $"Add-Type -AssemblyName System.Speech; (New-Object System.Speech.Synthesis.SpeechSynthesizer).Speak('{text}')",
                    }, timeoutMs: 120000);
                    return new ActionResult(req.Name, r.Code == 0, r.Code == 0 ? "OK" : r.Output.Trim());
                }
                default:
                    return new ActionResult(req.Name, false, "Na Windows nepodporováno: " + req.Name);
            }
        }
        catch (Exception ex)
        {
            return new ActionResult(req.Name, false, ex.Message);
        }
    }

    private static ActionResult Ok(ActionRequest req, string output) => new(req.Name, true, output);

    private static async Task<ActionResult> Shutdown(ActionRequest req, string mode)
    {
        var r = await ProcessUtil.RunAsync("shutdown.exe", mode == "/l" ? new[] { "/l" } : new[] { mode, "/t", "0" });
        return new ActionResult(req.Name, r.Code == 0, r.Code == 0 ? "OK" : r.Output.Trim());
    }

    // ---------------- schránka, koš, terminál ----------------

    public Task<string> GetClipboardAsync() =>
        Task.FromResult(OnUi(() => Clipboard.ContainsText() ? Clipboard.GetText() : ""));

    public long ClipboardChangeCount() => GetClipboardSequenceNumber();

    public Task SetClipboardAsync(string text)
    {
        // konce řádků po windowsovsku (z Macu chodí \n)
        string win = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        OnUi(() => { if (win.Length > 0) Clipboard.SetText(win); else Clipboard.Clear(); });
        return Task.CompletedTask;
    }

    public Task<(bool Ok, string Message)> MoveToTrashAsync(string path)
    {
        try
        {
            OnUi(() =>
            {
                if (Directory.Exists(path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            });
            return Task.FromResult((true, "Přesunuto do koše"));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, ex.Message));
        }
    }

    public ProcessStartInfo CreateShell(string command, string workingDirectory)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command" })
            psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + command);
        return psi;
    }

    // ---------------- puštění souborů ----------------

    public Task<DropTarget> ResolveDropTargetAsync(double? x, double? y)
    {
        string home = HomeDir;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        try
        {
            if (x is double px && y is double py)
            {
                IntPtr hwnd = WindowFromPoint(new POINT { X = (int)px, Y = (int)py });
                IntPtr root = hwnd != IntPtr.Zero ? GetAncestor(hwnd, 2 /* GA_ROOT */) : IntPtr.Zero;
                if (root != IntPtr.Zero)
                {
                    var cls = new StringBuilder(256);
                    GetClassName(root, cls, cls.Capacity);
                    string className = cls.ToString();
                    if (className is "Progman" or "WorkerW") return Task.FromResult(new DropTarget(desktop, "Plocha"));

                    if (className == "CabinetWClass") // okno Průzkumníka
                    {
                        string? folder = ExplorerFolder(root);
                        if (folder != null) return Task.FromResult(new DropTarget(folder, "složka „" + Path.GetFileName(folder.TrimEnd('\\')) + "“"));
                        return Task.FromResult(new DropTarget(desktop, "Plocha"));
                    }

                    GetWindowThreadProcessId(root, out uint pid);
                    using var proc = Process.GetProcessById((int)pid);
                    string? exe = null;
                    try { exe = proc.MainModule?.FileName; } catch { /* proces s vyššími právy */ }
                    if (exe != null && !exe.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string dir = Path.Combine(home, "Downloads", "RemoteScreen", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
                        Directory.CreateDirectory(dir);
                        string label = proc.MainWindowTitle.Length > 0 ? proc.ProcessName : Path.GetFileNameWithoutExtension(exe);
                        return Task.FromResult(new DropTarget(dir, "aplikace " + label, exe));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Určení cíle pro soubory selhalo: " + ex.Message);
        }
        return Task.FromResult(new DropTarget(desktop, "Plocha"));
    }

    /// <summary>Složka tohoto PC pod bodem obrazovky: plocha nebo okno Průzkumníka. Jinak null.
    /// Používá klient, když uživatel pustí soubory z druhého počítače dřív, než se stihly stáhnout.</summary>
    internal static string? LocalFolderAt(System.Drawing.Point p)
    {
        try
        {
            IntPtr hwnd = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
            IntPtr root = hwnd != IntPtr.Zero ? GetAncestor(hwnd, 2 /* GA_ROOT */) : IntPtr.Zero;
            if (root == IntPtr.Zero) return null;
            var cls = new StringBuilder(256);
            GetClassName(root, cls, cls.Capacity);
            string className = cls.ToString();
            if (className is "Progman" or "WorkerW") return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (className == "CabinetWClass") return ExplorerFolder(root);
        }
        catch { }
        return null;
    }

    /// <summary>Složka otevřená v okně Průzkumníka (Shell.Application).</summary>
    internal static string? ExplorerFolder(IntPtr hwnd)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (new IntPtr((long)w.HWND) != hwnd) continue;
                    string path = w.Document.Folder.Self.Path;
                    return Directory.Exists(path) ? path : null;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ---------------- tažení souborů z Windows ven ----------------
    // Windows nemá veřejnou „schránku tažení“ jako macOS. Proto si při stisku tlačítka zapamatujeme,
    // nad jakým oknem Průzkumníka (nebo plochou) bylo. Když pak klient ohlásí, že myš opustila obraz se
    // stisknutým tlačítkem, vezmeme označené položky v tom okně – právě ty Průzkumník táhne.

    private long _dragCounter;
    private IntPtr _dragRoot;
    private string? _dragRootClass;

    public long DragChangeCount()
    {
        // volá se při stisku levého tlačítka (výchozí bod) a při dotazu na tažení
        try
        {
            if (GetCursorPos(out var pt))
            {
                IntPtr hwnd = WindowFromPoint(pt);
                IntPtr root = hwnd != IntPtr.Zero ? GetAncestor(hwnd, 2 /* GA_ROOT */) : IntPtr.Zero;
                if ((GetAsyncKeyState(0x01 /* VK_LBUTTON */) & 0x8000) == 0)
                {
                    // tlačítko ještě není dole (stisk teprve přijde) → zapamatovat okno pod kurzorem
                    _dragRoot = root;
                    var cls = new StringBuilder(256);
                    if (root != IntPtr.Zero) GetClassName(root, cls, cls.Capacity);
                    _dragRootClass = cls.ToString();
                }
            }
        }
        catch { }
        return Interlocked.Increment(ref _dragCounter);
    }

    public List<string> DragFilePaths()
    {
        var result = new List<string>();
        try
        {
            if ((GetAsyncKeyState(0x01) & 0x8000) == 0) return result; // tlačítko už je puštěné → žádné tažení
            string? cls = _dragRootClass;
            if (cls is not ("CabinetWClass" or "Progman" or "WorkerW")) return result;

            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return result;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic? view = null;
            if (cls == "CabinetWClass")
            {
                foreach (dynamic w in shell.Windows())
                {
                    try { if (new IntPtr((long)w.HWND) == _dragRoot) { view = w.Document; break; } } catch { }
                }
            }
            else
            {
                // plocha: ShellWindows.FindWindowSW(CSIDL_DESKTOP, …, SWC_DESKTOP, …, SWFO_NEEDDISPATCH)
                object loc = 0, root = Type.Missing;
                int hwnd = 0;
                dynamic desk = shell.Windows().FindWindowSW(ref loc, ref root, 8, ref hwnd, 1);
                view = desk?.Document;
            }
            if (view == null) return result;
            foreach (dynamic item in view.SelectedItems())
            {
                try
                {
                    string path = item.Path;
                    if (File.Exists(path) || Directory.Exists(path)) result.Add(path);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Zjištění tažených souborů selhalo: " + ex.Message);
        }
        return result;
    }
}
