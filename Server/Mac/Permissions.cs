using System.Diagnostics;

namespace RemoteScreen.Server;

/// <summary>Oprávnění macOS (TCC) – zjištění stavu a otevření příslušné sekce Nastavení.</summary>
internal static class Permissions
{
    public static bool ScreenRecording
    {
        get { try { return Native.CGPreflightScreenCaptureAccess(); } catch { return false; } }
    }

    public static bool Accessibility
    {
        get { try { return Native.AXIsProcessTrusted(); } catch { return false; } }
    }

    /// <summary>Plný přístup k disku – zkouška čtení databáze TCC, kterou bez něj nelze otevřít.</summary>
    public static bool FullDiskAccess
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string probe = Path.Combine(home, "Library", "Application Support", "com.apple.TCC", "TCC.db");
            try
            {
                using var fs = File.OpenRead(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Při startu: požádat o chybějící oprávnění (macOS ukáže systémový dotaz a přidá aplikaci do seznamu).</summary>
    public static void RequestMissing()
    {
        if (!ScreenRecording)
        {
            Log.Warn("Chybí oprávnění Nahrávání obrazovky.");
            try { Native.CGRequestScreenCaptureAccess(); } catch { }
        }
        if (!Accessibility)
        {
            Log.Warn("Chybí oprávnění Zpřístupnění.");
            PromptAccessibility();
        }
    }

    public static void PromptAccessibility()
    {
        try
        {
            var opts = Native.CreateDictionary(Native.AXTrustedCheckOptionPrompt, Native.CFBooleanTrue);
            Native.AXIsProcessTrustedWithOptions(opts);
            Native.CFRelease(opts);
        }
        catch { }
    }

    public static void OpenScreenRecordingSettings()
    {
        try { Native.CGRequestScreenCaptureAccess(); } catch { }
        Open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture");
    }

    public static void OpenAccessibilitySettings()
    {
        PromptAccessibility();
        Open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility");
    }

    public static void OpenFullDiskAccessSettings() =>
        Open("x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles");

    public static void Open(string target)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Nelze otevřít {target}: {ex.Message}");
        }
    }
}
