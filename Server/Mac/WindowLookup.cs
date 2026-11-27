using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Zjištění, nad kterým oknem na Macu uživatel pustil soubory, a kam je tedy uložit.</summary>
internal static class WindowLookup
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public sealed record HitWindow(int Pid, string Owner, int WindowId);

    /// <summary>Nejvyšší běžné okno (vrstva 0) pod bodem v globálních souřadnicích (body, počátek vlevo nahoře).</summary>
    public static HitWindow? At(double x, double y)
    {
        IntPtr list = Native.CGWindowListCopyWindowInfo(1 | 16, 0); // OnScreenOnly | ExcludeDesktopElements
        if (list == IntPtr.Zero) return null;
        try
        {
            var cg = Native.CoreGraphicsLib.Value;
            IntPtr kLayer = Native.ReadConstant(cg, "kCGWindowLayer");
            IntPtr kBounds = Native.ReadConstant(cg, "kCGWindowBounds");
            IntPtr kPid = Native.ReadConstant(cg, "kCGWindowOwnerPID");
            IntPtr kOwner = Native.ReadConstant(cg, "kCGWindowOwnerName");
            IntPtr kNumber = Native.ReadConstant(cg, "kCGWindowNumber");

            nint n = Native.CFArrayGetCount(list);
            for (nint i = 0; i < n; i++) // pořadí je odpředu dozadu
            {
                IntPtr dict = Native.CFArrayGetValueAtIndex(list, i);
                if (!Native.CFNumberGetValue(Native.CFDictionaryGetValue(dict, kLayer), 3, out int layer) || layer != 0) continue;
                IntPtr b = Native.CFDictionaryGetValue(dict, kBounds);
                if (b == IntPtr.Zero || !Native.CGRectMakeWithDictionaryRepresentation(b, out var r)) continue;
                if (r.Size.Width < 40 || r.Size.Height < 40) continue; // drobná neviditelná okna
                if (x < r.Origin.X || x >= r.Origin.X + r.Size.Width || y < r.Origin.Y || y >= r.Origin.Y + r.Size.Height) continue;

                Native.CFNumberGetValue(Native.CFDictionaryGetValue(dict, kPid), 3, out int pid);
                string owner = Native.CFStringToString(Native.CFDictionaryGetValue(dict, kOwner)) ?? "?";
                Native.CFNumberGetValue(Native.CFDictionaryGetValue(dict, kNumber), 3, out int number);
                return new HitWindow(pid, owner, number);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Zjištění okna pod kurzorem selhalo: " + ex.Message);
        }
        finally
        {
            Native.CFRelease(list);
        }
        return null;
    }

    /// <summary>Kam uložit soubory puštěné v bodě (x, y). Bez bodu: okno Finderu v popředí, jinak Plocha.</summary>
    public static async Task<DropTarget> ResolveAsync(double? x, double? y)
    {
        string desktop = Path.Combine(Home, "Desktop");
        try
        {
            if (x is double px && y is double py)
            {
                var hit = At(px, py);
                if (hit == null) return new DropTarget(desktop, "Plocha");

                Log.Info($"Puštění souborů v bodě {px:0},{py:0}: okno {hit.Owner} #{hit.WindowId}");
                if (hit.Owner == "Finder")
                {
                    var (folder, error) = await FinderFolderAtAsync(px, py, hit.WindowId);
                    if (folder == null && error == null)
                    {
                        // první dotaz na Finder po spuštění bývá pomalý nebo selže (Finder teprve načítá
                        // skriptovací slovník) – zkusit znovu, než to vzdáme a uložíme na Plochu
                        await Task.Delay(300);
                        (folder, error) = await FinderFolderAtAsync(px, py, hit.WindowId);
                    }
                    if (folder != null && folder != desktop)
                        return new DropTarget(folder, "složka „" + Path.GetFileName(folder) + "“");
                    if (error != null)
                        return new DropTarget(desktop, "Plocha – " + error);
                    return new DropTarget(desktop, folder == desktop ? "Plocha" : "Plocha (Finder nevrátil složku okna)");
                }

                string? app = AppBundlePath(hit.Pid);
                if (app != null)
                {
                    // soubory pro aplikaci do vlastní složky ve Stažených – bez kolizí názvů
                    string dir = Path.Combine(Home, "Downloads", "RemoteScreen", DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
                    Directory.CreateDirectory(dir);
                    return new DropTarget(dir, $"aplikace {hit.Owner}", app);
                }
                return new DropTarget(desktop, "Plocha");
            }

            // bez souřadnic (tlačítko „Nahrát na Mac“): složka Finderu v popředí, jinak Plocha
            var (code, output) = await SystemActions.RunProcessAsync("/usr/bin/osascript", new[]
            {
                "-e", "tell application \"System Events\" to set fa to name of first application process whose frontmost is true",
                "-e", "if fa is \"Finder\" then",
                "-e", "tell application \"Finder\"",
                "-e", "if (count of Finder windows) > 0 then return POSIX path of (target of front Finder window as alias)",
                "-e", "end tell",
                "-e", "end if",
                "-e", "return \"\"",
            }, timeoutMs: 15000, mergeStdErr: false);
            string path = NormalizeDir(output);
            if (code == 0 && path.Length > 0 && path != desktop)
                return new DropTarget(path, "složka „" + Path.GetFileName(path) + "“");
        }
        catch (Exception ex)
        {
            Log.Warn("Určení cíle pro soubory selhalo: " + ex.Message);
        }
        return new DropTarget(desktop, "Plocha");
    }

    private static int _warm;

    /// <summary>„Probudí“ skriptování Finderu na pozadí (volá se při připojení klienta), aby první
    /// puštění souborů nečekalo na studený start osascriptu a neskončilo na Ploše.</summary>
    public static void Warmup()
    {
        if (Interlocked.Exchange(ref _warm, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await SystemActions.RunProcessAsync("/usr/bin/osascript", new[]
                {
                    "-e", "tell application \"Finder\"",
                    "-e", "try",
                    "-e", "if (count of Finder windows) > 0 then get POSIX path of (target of front Finder window as alias)",
                    "-e", "end try",
                    "-e", "end tell",
                }, timeoutMs: 20000);
            }
            catch { }
            finally { Interlocked.Exchange(ref _warm, 0); }
        });
    }

    /// <summary>Složka okna Finderu, nad kterým byly soubory puštěny.
    /// Postupně: podle čísla okna (= id okna ve Finderu), podle polohy, nakonec přední okno Finderu.</summary>
    private static async Task<(string? Path, string? Error)> FinderFolderAtAsync(double x, double y, int windowId)
    {
        string px = ((int)x).ToString(), py = ((int)y).ToString();
        var (code, output) = await SystemActions.RunProcessAsync("/usr/bin/osascript", new[]
        {
            "-e", $"set wid to {windowId}",
            "-e", $"set px to {px}",
            "-e", $"set py to {py}",
            "-e", "set res to \"\"",
            "-e", "tell application \"Finder\"",
            "-e", "try",
            "-e", "set res to POSIX path of (target of (first Finder window whose id is wid) as alias)",
            "-e", "end try",
            "-e", "if res is \"\" then",
            "-e", "repeat with w in Finder windows",
            "-e", "try",
            "-e", "set b to bounds of w",
            "-e", "if px >= item 1 of b and px <= item 3 of b and py >= item 2 of b and py <= item 4 of b then",
            "-e", "set res to POSIX path of (target of w as alias)",
            "-e", "exit repeat",
            "-e", "end if",
            "-e", "end try",
            "-e", "end repeat",
            "-e", "end if",
            "-e", "if res is \"\" then",
            "-e", "try",
            "-e", "set res to POSIX path of (target of front Finder window as alias)",
            "-e", "end try",
            "-e", "end if",
            "-e", "end tell",
            "-e", "return res",
        }, timeoutMs: 9000, mergeStdErr: true);

        if (code != 0)
        {
            Log.Warn($"Složku okna Finderu nelze zjistit (kód {code}): {output.Trim()}");
            if (output.Contains("-1743") || output.Contains("Not authorized") || output.Contains("nemá oprávnění"))
                return (null, "macOS nepovolil RemoteScreen ovládat Finder (Nastavení → Soukromí a zabezpečení → Automatizace → RemoteScreen → Finder)");
            return (null, null);
        }
        string path = NormalizeDir(output);
        if (path.Length == 0) Log.Warn($"Okno Finderu {windowId} nemá běžnou složku (např. Nedávné nebo AirDrop) – ukládám na Plochu.");
        return (path.Length > 0 ? path : null, null);
    }

    private static string NormalizeDir(string output)
    {
        string p = output.Trim();
        if (p.Length == 0 || !Directory.Exists(p)) return "";
        p = Path.GetFullPath(p).TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    /// <summary>Cesta k .app podle PID (NSRunningApplication).</summary>
    private static string? AppBundlePath(int pid)
    {
        if (!ObjC.LoadAppKit()) return null;
        IntPtr pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            IntPtr app = ObjC.SendInt(ObjC.Cls("NSRunningApplication"), ObjC.Sel("runningApplicationWithProcessIdentifier:"), pid);
            if (app == IntPtr.Zero) return null;
            IntPtr url = ObjC.Send(app, ObjC.Sel("bundleURL"));
            if (url == IntPtr.Zero) return null;
            IntPtr path = ObjC.Send(url, ObjC.Sel("path"));
            string? s = Native.CFStringToString(path);
            return s != null && s.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? s : null;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }
}
