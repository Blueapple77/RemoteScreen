using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Vkládá události myši a klávesnice do macOS. Vyžaduje oprávnění Zpřístupnění (Accessibility).</summary>
internal sealed class InputInjector : IInputSink
{
    private readonly object _gate = new();
    private ulong _flags;
    private bool _left, _right, _other;
    private CGPoint _pos;
    private readonly HashSet<ushort> _keysDown = new();
    private short _scrollRemainderY, _scrollRemainderX;

    /// <summary>Displej, ke kterému se vztahují normalizované souřadnice (0..1).</summary>
    public DisplayInfo Display { get; set; }

    public InputInjector(DisplayInfo display) => Display = display;

    private CGPoint Map(float nx, float ny)
    {
        var d = Display;
        double x = d.X + Math.Clamp(nx, 0f, 1f) * (d.Width - 1);
        double y = d.Y + Math.Clamp(ny, 0f, 1f) * (d.Height - 1);
        return new CGPoint(x, y);
    }

    public void MouseMove(float nx, float ny)
    {
        lock (_gate)
        {
            _pos = Map(nx, ny);
            (uint type, uint button) =
                _left ? (CGConst.LeftMouseDragged, CGConst.ButtonLeft) :
                _right ? (CGConst.RightMouseDragged, CGConst.ButtonRight) :
                _other ? (CGConst.OtherMouseDragged, CGConst.ButtonCenter) :
                (CGConst.MouseMoved, CGConst.ButtonLeft);
            PostMouse(type, button, 0);
        }
    }

    public void MouseButton(byte button, bool down, int clicks, float nx, float ny)
    {
        lock (_gate)
        {
            _pos = Map(nx, ny);
            uint type, cgButton;
            switch (button)
            {
                case 0:
                    _left = down;
                    type = down ? CGConst.LeftMouseDown : CGConst.LeftMouseUp;
                    cgButton = CGConst.ButtonLeft;
                    break;
                case 1:
                    _right = down;
                    type = down ? CGConst.RightMouseDown : CGConst.RightMouseUp;
                    cgButton = CGConst.ButtonRight;
                    break;
                default:
                    _other = down;
                    type = down ? CGConst.OtherMouseDown : CGConst.OtherMouseUp;
                    cgButton = CGConst.ButtonCenter;
                    break;
            }
            PostMouse(type, cgButton, Math.Clamp(clicks, 1, 3));
        }
    }

    /// <summary>Hodnota pole kCGEventSourceUserData, kterou se označí vložené události. Okno klienta
    /// je pak nepřeposílá dál (jinak smyčka: PC ovládá Mac, který ovládá to PC).</summary>
    public const long InjectedMarker = 0x52534352; // "RSCR"
    public const uint FieldSourceUserData = 42, FieldSourcePid = 41;

    private static void Post(IntPtr ev)
    {
        Native.CGEventSetIntegerValueField(ev, FieldSourceUserData, InjectedMarker);
        Native.CGEventPost(CGConst.HIDEventTap, ev);
    }

    private void PostMouse(uint type, uint button, int clicks)
    {
        IntPtr ev = Native.CGEventCreateMouseEvent(IntPtr.Zero, type, _pos, button);
        if (ev == IntPtr.Zero) return;
        if (clicks > 0) Native.CGEventSetIntegerValueField(ev, CGConst.FieldMouseClickState, clicks);
        Native.CGEventSetFlags(ev, _flags);
        Post(ev);
        Native.CFRelease(ev);
    }

    /// <summary>Delta ve stylu Windows: 120 = jeden zub = cca 40 px.</summary>
    public void Scroll(short dy, short dx)
    {
        lock (_gate)
        {
            int totalY = dy + _scrollRemainderY, totalX = dx + _scrollRemainderX;
            int py = totalY / 3, px = totalX / 3;
            _scrollRemainderY = (short)(totalY - py * 3);
            _scrollRemainderX = (short)(totalX - px * 3);
            if (py == 0 && px == 0) return;

            IntPtr ev = Native.CGEventCreateScrollWheelEvent2(IntPtr.Zero, CGConst.ScrollUnitPixel, 2, py, px, 0);
            if (ev == IntPtr.Zero) return;
            Native.CGEventSetFlags(ev, _flags);
            Post(ev);
            Native.CFRelease(ev);
        }
    }

    private static ulong ModifierFlag(ushort code) => code switch
    {
        0x38 or 0x3C => CGConst.FlagShift,
        0x3B or 0x3E => CGConst.FlagControl,
        0x3A or 0x3D => CGConst.FlagAlternate,
        0x37 or 0x36 => CGConst.FlagCommand,
        _ => 0,
    };

    /// <summary>Příznaky rozlišující levou/pravou klávesu (NX_DEVICE*KEYMASK) – některé aplikace je kontrolují.</summary>
    private static ulong DeviceFlag(ushort code) => code switch
    {
        0x3B => 0x0001, // levý Control
        0x38 => 0x0002, // levý Shift
        0x3C => 0x0004, // pravý Shift
        0x37 => 0x0008, // levý Command
        0x36 => 0x0010, // pravý Command
        0x3A => 0x0020, // levý Option
        0x3D => 0x0040, // pravý Option
        0x3E => 0x2000, // pravý Control
        _ => 0,
    };

    private bool _capsOn;

    /// <summary>Přepočítá příznaky modifikátorů podle aktuálně držených kláves.</summary>
    private void RecomputeFlags()
    {
        ulong f = CGConst.FlagNonCoalesced;
        foreach (var k in _keysDown) f |= ModifierFlag(k) | DeviceFlag(k);
        if (_capsOn) f |= CGConst.FlagAlphaShift;
        _flags = f;
    }

    public void Key(ushort code, bool down)
    {
        lock (_gate)
        {
            bool wasDown = _keysDown.Contains(code);
            bool isModifier = ModifierFlag(code) != 0 || code == 0x39;

            if (ServerOptions.DebugKeys)
                Log.Info($"klávesa 0x{code:X2} {(down ? "dolů" : "nahoru")}{(isModifier ? " (modifikátor)" : "")}{(down && wasDown ? " opakování" : "")}");

            // modifikátory: opakované stisky (autorepeat z Windows) a duplicitní puštění ignorovat
            if (isModifier && down == wasDown) return;

            if (down) _keysDown.Add(code); else _keysDown.Remove(code);
            if (code == 0x39 && down) _capsOn = !_capsOn;
            RecomputeFlags();

            IntPtr ev = Native.CGEventCreateKeyboardEvent(IntPtr.Zero, code, down);
            if (ev == IntPtr.Zero) return;
            if (isModifier)
                Native.CGEventSetType(ev, CGConst.EventFlagsChanged); // macOS čeká u modifikátorů tento typ
            else if (down && wasDown)
                Native.CGEventSetIntegerValueField(ev, CGConst.FieldKeyboardAutorepeat, 1);
            Native.CGEventSetFlags(ev, _flags);
            Post(ev);
            Native.CFRelease(ev);
        }
    }

    /// <summary>Napíše libovolný Unicode text (diakritika, emoji…) bez ohledu na rozložení klávesnice.</summary>
    public void TypeText(string text)
    {
        text = text.Replace("\r\n", "\n");
        lock (_gate)
        {
            // Enter a Tab jako skutečné klávesy, aby fungovaly ve všech aplikacích
            foreach (var whole in SplitSpecial(text))
            {
                if (whole == "\n") { TapKeyNoLock(0x24); continue; }
                if (whole == "\t") { TapKeyNoLock(0x30); continue; }

                const int chunk = 16;
                for (int i = 0; i < whole.Length;)
                {
                    int len = Math.Min(chunk, whole.Length - i);
                    if (len < whole.Length - i && char.IsHighSurrogate(whole[i + len - 1])) len++; // nerozdělit emoji
                    var chars = whole.Substring(i, len).Select(c => (ushort)c).ToArray();
                    i += len;
                    foreach (bool down in new[] { true, false })
                    {
                        IntPtr ev = Native.CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down);
                        if (ev == IntPtr.Zero) continue;
                        Native.CGEventSetFlags(ev, 0);
                        Native.CGEventKeyboardSetUnicodeString(ev, (nuint)chars.Length, chars);
                        Post(ev);
                        Native.CFRelease(ev);
                    }
                    Thread.Sleep(4);
                }
            }
        }
    }

    private static IEnumerable<string> SplitSpecial(string s)
    {
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is '\n' or '\t')
            {
                if (i > start) yield return s[start..i];
                yield return s[i].ToString();
                start = i + 1;
            }
        }
        if (start < s.Length) yield return s[start..];
    }

    private void TapKeyNoLock(ushort code, ulong flags = 0)
    {
        foreach (bool down in new[] { true, false })
        {
            IntPtr ev = Native.CGEventCreateKeyboardEvent(IntPtr.Zero, code, down);
            if (ev == IntPtr.Zero) continue;
            Native.CGEventSetFlags(ev, flags);
            Post(ev);
            Native.CFRelease(ev);
        }
    }

    /// <summary>Stiskne kombinaci kláves (modifikátory + klávesa).</summary>
    public void Combo(ulong flags, ushort key)
    {
        lock (_gate) TapKeyNoLock(key, flags);
    }

    /// <summary>Ctrl+Cmd+Q – zamknutí obrazovky.</summary>
    public void LockScreen() => Combo(CGConst.FlagControl | CGConst.FlagCommand, 0x0C);

    /// <summary>Uvolní všechny držené klávesy a tlačítka (např. při ztrátě fokusu u klienta).</summary>
    public void ReleaseAll()
    {
        ushort[] keys;
        lock (_gate) keys = _keysDown.ToArray();
        foreach (var k in keys) Key(k, false);

        lock (_gate)
        {
            if (_left) { _left = false; PostMouse(CGConst.LeftMouseUp, CGConst.ButtonLeft, 1); }
            if (_right) { _right = false; PostMouse(CGConst.RightMouseUp, CGConst.ButtonRight, 1); }
            if (_other) { _other = false; PostMouse(CGConst.OtherMouseUp, CGConst.ButtonCenter, 1); }
            RecomputeFlags();
        }
    }
}
