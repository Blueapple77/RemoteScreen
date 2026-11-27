using RemoteScreen.Host;
using RemoteScreen.Shared;
using static RemoteScreen.Client.Host.WinNative;

namespace RemoteScreen.Client.Host;

/// <summary>Myš a klávesnice na tomto PC (SendInput). Klávesy přicházejí jako kódy macOS a převádějí se na scan kódy.</summary>
internal sealed class WinInput : IInputSink
{
    private readonly object _gate = new();
    private readonly HashSet<ushort> _keysDown = new();
    private bool _left, _right, _middle;

    public DisplayInfo Display { get; set; }

    public WinInput(DisplayInfo display) => Display = display;

    private (int X, int Y) ToAbsolute(float nx, float ny)
    {
        var d = Display;
        double px = d.X + Math.Clamp(nx, 0f, 1f) * (d.Width - 1);
        double py = d.Y + Math.Clamp(ny, 0f, 1f) * (d.Height - 1);
        var vs = SystemInformation.VirtualScreen;
        int ax = (int)Math.Round((px - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        int ay = (int)Math.Round((py - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        return (ax, ay);
    }

    public void MouseMove(float nx, float ny)
    {
        var (x, y) = ToAbsolute(nx, ny);
        lock (_gate) Send(Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));
    }

    public void MouseButton(byte button, bool down, int clicks, float nx, float ny)
    {
        var (x, y) = ToAbsolute(nx, ny);
        uint flag = button switch
        {
            1 => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            2 => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            _ => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
        };
        lock (_gate)
        {
            if (button == 0) _left = down; else if (button == 1) _right = down; else _middle = down;
            // dvojklik pozná Windows samo podle času mezi kliknutími
            Send(Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK),
                 Mouse(x, y, flag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK));
        }
    }

    public void Scroll(short dy, short dx)
    {
        lock (_gate)
        {
            if (dy != 0) Send(Mouse(0, 0, MOUSEEVENTF_WHEEL, unchecked((uint)(int)dy)));
            if (dx != 0) Send(Mouse(0, 0, MOUSEEVENTF_HWHEEL, unchecked((uint)(int)-dx))); // klient posílá obráceně (pro Mac)
        }
    }

    public void Key(ushort macKeyCode, bool down)
    {
        var k = KeyMap.FromMac(macKeyCode);
        if (k == null) return;
        lock (_gate)
        {
            if (down) _keysDown.Add(macKeyCode); else _keysDown.Remove(macKeyCode);
            if (ServerOptions.DebugKeys) Log.Info($"klávesa mac 0x{macKeyCode:X2} → scan 0x{k.Value.Scan:X2}{(k.Value.Extended ? " ext" : "")} {(down ? "dolů" : "nahoru")}");
            Send(KeyScan((ushort)k.Value.Scan, k.Value.Extended, down));
        }
    }

    public void TypeText(string text)
    {
        text = text.Replace("\r\n", "\n");
        lock (_gate)
        {
            foreach (char c in text)
            {
                if (c == '\r') continue;
                if (c == '\n') { Send(KeyVk(0x0D, true), KeyVk(0x0D, false)); continue; }
                if (c == '\t') { Send(KeyVk(0x09, true), KeyVk(0x09, false)); continue; }
                Send(KeyUnicode(c, true), KeyUnicode(c, false));
            }
        }
    }

    /// <summary>Klávesa podle virtuálního kódu Windows (hlasitost, média…).</summary>
    public void TapVk(params ushort[] vks)
    {
        lock (_gate)
        {
            foreach (var vk in vks) Send(KeyVk(vk, true));
            foreach (var vk in vks.Reverse()) Send(KeyVk(vk, false));
        }
    }

    public void ReleaseAll()
    {
        ushort[] keys;
        lock (_gate) keys = _keysDown.ToArray();
        foreach (var k in keys) Key(k, false);
        lock (_gate)
        {
            if (_left) Send(Mouse(0, 0, MOUSEEVENTF_LEFTUP));
            if (_right) Send(Mouse(0, 0, MOUSEEVENTF_RIGHTUP));
            if (_middle) Send(Mouse(0, 0, MOUSEEVENTF_MIDDLEUP));
            _left = _right = _middle = false;
        }
    }
}
