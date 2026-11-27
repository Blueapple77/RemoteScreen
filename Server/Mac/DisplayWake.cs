namespace RemoteScreen.Server;

/// <summary>Rozsvícení displeje. Po probuzení přes síť (Wake-on-LAN) nechává macOS displej vypnutý
/// a nahrává se jen černá plocha – zamykací obrazovka se ukáže až po „aktivitě uživatele“.</summary>
internal static class DisplayWake
{
    private static readonly object Gate = new();
    private static readonly IntPtr Name = Native.CFStringCreateWithCString(IntPtr.Zero, "RemoteScreen – vzdálené ovládání", Native.kCFStringEncodingUTF8);
    private static uint _assertionId;
    private static DateTime _last = DateTime.MinValue;

    /// <param name="force">false = nejvýš jednou za 5 s (volá se i při pohybu myší)</param>
    public static void Wake(bool force = true)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - _last).TotalSeconds < 5) return;
            _last = now;
            try
            {
                int r = Native.IOPMAssertionDeclareUserActivity(Name, 0 /* kIOPMUserActiveLocal */, ref _assertionId);
                if (r != 0 && force) Log.Warn($"Rozsvícení displeje: IOKit vrátil 0x{r:X}");
            }
            catch (Exception ex)
            {
                if (force) Log.Warn("Rozsvícení displeje: " + ex.Message);
            }
        }
    }
}
