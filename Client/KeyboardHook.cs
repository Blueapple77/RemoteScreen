using System.Runtime.InteropServices;

namespace RemoteScreen.Client;

/// <summary>Nízkoúrovňový hook klávesnice – zachytí i Win, Alt+Tab apod., aby šly poslat na Mac.</summary>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>(scanCode, extended, keyUp, vkCode) → true = klávesu spolknout (nepředat Windows)</summary>
    public Func<int, bool, bool, int, bool>? Handler { get; set; }

    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly LowLevelKeyboardProc _proc; // držet referenci, jinak ji GC uklidí
    private IntPtr _hook;

    public KeyboardHook()
    {
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Handler != null)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.flags & LLKHF_INJECTED) == 0)
            {
                bool up = (k.flags & LLKHF_UP) != 0;
                bool ext = (k.flags & LLKHF_EXTENDED) != 0;
                try
                {
                    if (Handler((int)k.scanCode, ext, up, (int)k.vkCode)) return 1;
                }
                catch { /* výjimka v hooku nesmí shodit systémový vstup */ }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
