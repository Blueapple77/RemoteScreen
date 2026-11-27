namespace RemoteScreen.Client;

/// <summary>Převod fyzických kláves Windows (scan kódy, sada 1) na virtuální kódy macOS (kVK_*).
/// Převádí se podle pozice klávesy, takže znaky určuje rozložení klávesnice nastavené na Macu.</summary>
public static class KeyMap
{
    private const int Ext = 0x100;

    private static readonly Dictionary<int, ushort> Map = new()
    {
        // písmena
        [0x1E] = 0x00, // A
        [0x1F] = 0x01, // S
        [0x20] = 0x02, // D
        [0x21] = 0x03, // F
        [0x23] = 0x04, // H
        [0x22] = 0x05, // G
        [0x2C] = 0x06, // Z (pozice US)
        [0x2D] = 0x07, // X
        [0x2E] = 0x08, // C
        [0x2F] = 0x09, // V
        [0x30] = 0x0B, // B
        [0x10] = 0x0C, // Q
        [0x11] = 0x0D, // W
        [0x12] = 0x0E, // E
        [0x13] = 0x0F, // R
        [0x15] = 0x10, // Y (pozice US)
        [0x14] = 0x11, // T
        [0x17] = 0x22, // I
        [0x16] = 0x20, // U
        [0x18] = 0x1F, // O
        [0x19] = 0x23, // P
        [0x26] = 0x25, // L
        [0x24] = 0x26, // J
        [0x25] = 0x28, // K
        [0x31] = 0x2D, // N
        [0x32] = 0x2E, // M

        // číselná řada
        [0x02] = 0x12, // 1
        [0x03] = 0x13, // 2
        [0x04] = 0x14, // 3
        [0x05] = 0x15, // 4
        [0x06] = 0x17, // 5
        [0x07] = 0x16, // 6
        [0x08] = 0x1A, // 7
        [0x09] = 0x1C, // 8
        [0x0A] = 0x19, // 9
        [0x0B] = 0x1D, // 0
        [0x0C] = 0x1B, // -
        [0x0D] = 0x18, // =

        // interpunkce
        [0x1A] = 0x21, // [
        [0x1B] = 0x1E, // ]
        [0x27] = 0x29, // ;
        [0x28] = 0x27, // '
        [0x2B] = 0x2A, // \
        [0x33] = 0x2B, // ,
        [0x34] = 0x2F, // .
        [0x35] = 0x2C, // /
        [0x29] = 0x32, // ` (vlevo od 1)
        [0x56] = 0x0A, // ISO klávesa vlevo od Z

        // ovládací
        [0x1C] = 0x24, // Enter
        [0x0F] = 0x30, // Tab
        [0x39] = 0x31, // mezerník
        [0x0E] = 0x33, // Backspace
        [0x01] = 0x35, // Esc
        [0x3A] = 0x39, // Caps Lock

        // modifikátory (Win = Cmd, Alt = Option)
        [0x2A] = 0x38,        // levý Shift
        [0x36] = 0x3C,        // pravý Shift
        [Ext | 0x36] = 0x3C,  // pravý Shift (některé klávesnice ho hlásí jako rozšířený)
        [Ext | 0x2A] = 0x38,  // levý Shift hlášený jako rozšířený
        [0x1D] = 0x3B,        // levý Ctrl → Control
        [Ext | 0x1D] = 0x3E,  // pravý Ctrl → pravý Control (běžně je to hostitelská klávesa)
        [0x38] = 0x3A,        // levý Alt → Option
        [Ext | 0x38] = 0x3D,  // pravý Alt (AltGr) → pravý Option
        [Ext | 0x5B] = 0x37,  // levý Win → Command
        [Ext | 0x5C] = 0x36,  // pravý Win → pravý Command

        // F-klávesy
        [0x3B] = 0x7A, [0x3C] = 0x78, [0x3D] = 0x63, [0x3E] = 0x76, [0x3F] = 0x60, [0x40] = 0x61,
        [0x41] = 0x62, [0x42] = 0x64, [0x43] = 0x65, [0x44] = 0x6D, [0x57] = 0x67, [0x58] = 0x6F,

        // navigace
        [Ext | 0x52] = 0x72, // Insert → Help
        [Ext | 0x47] = 0x73, // Home
        [Ext | 0x49] = 0x74, // Page Up
        [Ext | 0x53] = 0x75, // Delete → Forward Delete
        [Ext | 0x4F] = 0x77, // End
        [Ext | 0x51] = 0x79, // Page Down
        [Ext | 0x4B] = 0x7B, // šipka vlevo
        [Ext | 0x4D] = 0x7C, // šipka vpravo
        [Ext | 0x50] = 0x7D, // šipka dolů
        [Ext | 0x48] = 0x7E, // šipka nahoru

        // numerická klávesnice
        [Ext | 0x45] = 0x47,  // Num Lock → Clear (v hooku hlášen jako rozšířená klávesa)
        [Ext | 0x35] = 0x4B,  // /
        [0x37] = 0x43,        // *
        [0x4A] = 0x4E,        // -
        [0x4E] = 0x45,        // +
        [Ext | 0x1C] = 0x4C,  // Enter
        [0x53] = 0x41,        // ,
        [0x52] = 0x52, [0x4F] = 0x53, [0x50] = 0x54, [0x51] = 0x55, [0x4B] = 0x56,
        [0x4C] = 0x57, [0x4D] = 0x58, [0x47] = 0x59, [0x48] = 0x5B, [0x49] = 0x5C,
    };

    private static readonly Lazy<Dictionary<ushort, (int Scan, bool Extended)>> Reverse = new(() =>
    {
        var r = new Dictionary<ushort, (int, bool)>();
        foreach (var (key, mac) in Map)
        {
            bool ext = (key & Ext) != 0;
            // u kódů s více variantami (Shift) dát přednost běžné, nerozšířené klávese
            if (r.TryGetValue(mac, out var existing) && (!existing.Item2 || ext)) continue;
            r[mac] = (key & 0xFF, ext);
        }
        r[0x3E] = (0x1D, true);   // pravý Control
        r[0x36] = (0x5C, true);   // pravý Command → pravý Win
        return r;
    });

    /// <summary>Opačný převod pro sdílení tohoto PC: kód macOS → scan kód Windows.</summary>
    public static (int Scan, bool Extended)? FromMac(ushort macKeyCode) =>
        Reverse.Value.TryGetValue(macKeyCode, out var v) ? v : null;

    /// <param name="swapCtrlCmd">levý Ctrl ↔ Win, aby Ctrl+C/V fungovalo jako na Windows</param>
    public static ushort? ToMac(int scanCode, bool extended, bool swapCtrlCmd)
    {
        int key = (scanCode & 0xFF) | (extended ? Ext : 0);
        if (swapCtrlCmd)
        {
            if (key == 0x1D) return 0x37;          // levý Ctrl → Command
            if (key == (Ext | 0x5B)) return 0x3B;  // levý Win → Control
        }
        return Map.TryGetValue(key, out var mac) ? mac : null;
    }
}
