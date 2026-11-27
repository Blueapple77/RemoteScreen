using System.Runtime.InteropServices;

namespace RemoteScreen.Server;

/// <summary>Spouštění po přihlášení. macOS 13+: SMAppService (aplikace se objeví v Nastavení → Obecné → Položky při přihlášení).
/// Starší macOS: LaunchAgent v ~/Library/LaunchAgents.</summary>
internal static unsafe class LoginItem
{
    private const string Label = "cz.remotescreen.app";

    private static string PlistPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    private static IntPtr Service()
    {
        try
        {
            NativeLibrary.TryLoad("/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement", out _);
            IntPtr cls = ObjC.Cls("SMAppService");
            if (cls == IntPtr.Zero || !ObjC.RespondsTo(cls, "mainAppService"))
            {
                Log.Info("SMAppService není k dispozici – použije se LaunchAgent.");
                return IntPtr.Zero;
            }
            return ObjC.Send(cls, ObjC.Sel("mainAppService"));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>0 = nezaregistrováno, 1 = zapnuto, 2 = čeká na schválení v Nastavení, 3 = nenalezeno</summary>
    private static long Status(IntPtr svc) => ObjC.SendRetInt(svc, ObjC.Sel("status"));

    public static bool IsEnabled
    {
        get
        {
            IntPtr svc = Service();
            if (svc != IntPtr.Zero) return Status(svc) is 1 or 2;
            return File.Exists(PlistPath);
        }
    }

    public static bool NeedsApproval
    {
        get
        {
            IntPtr svc = Service();
            return svc != IntPtr.Zero && Status(svc) == 2;
        }
    }

    public static void Set(bool enabled)
    {
        IntPtr svc = Service();
        if (svc != IntPtr.Zero)
        {
            IntPtr error = IntPtr.Zero;
            bool ok = ObjC.SendRetBool(svc, ObjC.Sel(enabled ? "registerAndReturnError:" : "unregisterAndReturnError:"), (IntPtr)(&error));
            if (!ok)
            {
                string msg = error != IntPtr.Zero ? Describe(error) : "neznámá chyba";
                Log.Warn($"Položku při přihlášení nelze {(enabled ? "zapnout" : "vypnout")}: {msg}");
            }
            Log.Info($"Položka při přihlášení: {(enabled ? "zapnout" : "vypnout")} → {(ok ? "OK" : "chyba")}, stav {Status(svc)}");
            if (enabled && Status(svc) == 2)
                Permissions.Open("x-apple.systempreferences:com.apple.LoginItems-Settings.extension");
            return;
        }

        // starší macOS – LaunchAgent, který aplikaci otevře po přihlášení
        if (enabled)
        {
            string app = AppBundlePath() ?? "/Applications/RemoteScreen.app";
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key><string>{Label}</string>
                    <key>ProgramArguments</key>
                    <array><string>/usr/bin/open</string><string>-g</string><string>{app}</string></array>
                    <key>RunAtLoad</key><true/>
                </dict>
                </plist>
                """);
        }
        else
        {
            try { File.Delete(PlistPath); } catch { }
        }
    }

    private static string Describe(IntPtr nsError)
    {
        try
        {
            IntPtr desc = ObjC.Send(nsError, ObjC.Sel("localizedDescription"));
            IntPtr utf8 = ObjC.Send(desc, ObjC.Sel("UTF8String"));
            return Marshal.PtrToStringUTF8(utf8) ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    /// <summary>Cesta k RemoteScreen.app, pokud běžíme z balíčku aplikace.</summary>
    public static string? AppBundlePath()
    {
        string exe = Environment.ProcessPath ?? "";
        int i = exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
        return i > 0 ? exe[..(i + 4)] : null;
    }
}
