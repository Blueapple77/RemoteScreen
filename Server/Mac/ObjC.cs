using System.Runtime.InteropServices;

namespace RemoteScreen.Server;

/// <summary>Minimální most do Objective-C runtime (AppKit) bez externích knihoven.
/// objc_msgSend se volá s přesným prototypem pro každou kombinaci parametrů – na arm64 to tak být musí.</summary>
internal static class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";

    [DllImport(Lib)] public static extern IntPtr objc_getClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern IntPtr sel_registerName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Lib)] public static extern IntPtr objc_allocateClassPair(IntPtr superclass, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint extraBytes);
    [DllImport(Lib)] public static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Lib)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, [MarshalAs(UnmanagedType.LPUTF8Str)] string types);

    [DllImport("/usr/lib/libSystem.B.dylib")] public static extern int pthread_main_np();

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SendRetBool0(IntPtr receiver, IntPtr selector);

    [DllImport(Lib)] public static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Lib)] public static extern void objc_autoreleasePoolPop(IntPtr pool);

    // --- objc_msgSend s různými prototypy ---
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a, IntPtr b, IntPtr c);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendDouble(IntPtr receiver, IntPtr selector, double a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendInt(IntPtr receiver, IntPtr selector, nint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.U1)] bool a);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern void SendSelObjBool(IntPtr receiver, IntPtr selector, IntPtr sel, IntPtr obj, [MarshalAs(UnmanagedType.U1)] bool wait);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nint SendRetInt(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGPoint SendRetPoint(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGSize SendRetSize(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendIntObj(IntPtr receiver, IntPtr selector, nint a, IntPtr b);

    /// <summary>nextEventMatchingMask:untilDate:inMode:dequeue:</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendNextEvent(IntPtr receiver, IntPtr selector, ulong mask, IntPtr untilDate, IntPtr mode, [MarshalAs(UnmanagedType.U1)] bool dequeue);

    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SendRetBool(IntPtr receiver, IntPtr selector, IntPtr a);

    // --- prototypy pro okna (ovládací okno klienta) ---
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendRect(IntPtr receiver, IntPtr selector, CGRect r);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendRectObj(IntPtr receiver, IntPtr selector, CGRect r, IntPtr a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendRectBool(IntPtr receiver, IntPtr selector, CGRect r, [MarshalAs(UnmanagedType.U1)] bool a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendSize(IntPtr receiver, IntPtr selector, CGSize s);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendObjSize(IntPtr receiver, IntPtr selector, IntPtr a, CGSize s);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendPoint(IntPtr receiver, IntPtr selector, CGPoint p);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern CGPoint SendPointObjRetPoint(IntPtr receiver, IntPtr selector, CGPoint p, IntPtr view);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendObjPoint(IntPtr receiver, IntPtr selector, IntPtr a, CGPoint p);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendDouble2(IntPtr receiver, IntPtr selector, double a, double b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendDouble4(IntPtr receiver, IntPtr selector, double a, double b, double c, double d);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendUInt(IntPtr receiver, IntPtr selector, nuint a);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendObjInt(IntPtr receiver, IntPtr selector, IntPtr a, nint b);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern IntPtr SendRetObjInt(IntPtr receiver, IntPtr selector, nuint index);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern double SendRetDouble(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern ushort SendRetUShort(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend")] public static extern nuint SendRetUInt(IntPtr receiver, IntPtr selector);

    /// <summary>initWithContentRect:styleMask:backing:defer:</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendInitWindow(IntPtr receiver, IntPtr selector, CGRect r, nuint style, nuint backing, [MarshalAs(UnmanagedType.U1)] bool defer);

    /// <summary>initWithRect:options:owner:userInfo: (NSTrackingArea)</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendInitTracking(IntPtr receiver, IntPtr selector, CGRect r, nuint options, IntPtr owner, IntPtr userInfo);

    /// <summary>scheduledTimerWithTimeInterval:target:selector:userInfo:repeats:</summary>
    [DllImport(Lib, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendTimer(IntPtr receiver, IntPtr selector, double interval, IntPtr target, IntPtr sel, IntPtr userInfo, [MarshalAs(UnmanagedType.U1)] bool repeats);

    [DllImport(Lib, EntryPoint = "objc_msgSend")] private static extern CGRect SendRetRectArm(IntPtr receiver, IntPtr selector);
    [DllImport(Lib, EntryPoint = "objc_msgSend_stret")] private static extern void SendRetRectX64(out CGRect result, IntPtr receiver, IntPtr selector);

    /// <summary>Zpráva vracející NSRect (bounds, frame…). Na Intel Macu se velké struktury vrací přes objc_msgSend_stret.</summary>
    public static CGRect SendRetRect(IntPtr receiver, IntPtr selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            SendRetRectX64(out var r, receiver, selector);
            return r;
        }
        return SendRetRectArm(receiver, selector);
    }

    public static CGRect Rect(IntPtr obj, string sel) => SendRetRect(obj, Sel(sel));

    /// <summary>[[Class alloc] initWithFrame:]</summary>
    public static IntPtr NewWithFrame(string cls, CGRect frame) => NewWithFrame(Cls(cls), frame);
    public static IntPtr NewWithFrame(IntPtr cls, CGRect frame) => SendRect(Send(cls, Sel("alloc")), Sel("initWithFrame:"), frame);

    /// <summary>NSColor v sRGB.</summary>
    public static IntPtr Color(double r, double g, double b, double a = 1) =>
        SendDouble4(Cls("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), r, g, b, a);

    public static IntPtr Font(double size, double weight = 0) =>
        SendDouble2(Cls("NSFont"), Sel("systemFontOfSize:weight:"), size, weight);

    /// <summary>.NET řetězec z NSString.</summary>
    public static string? ToStr(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero) return null;
        IntPtr utf8 = Send(nsString, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    public static IntPtr Get(IntPtr obj, string sel) => Send(obj, Sel(sel));
    public static void Set(IntPtr obj, string sel, IntPtr value) => Send(obj, Sel(sel), value);
    public static void SetBool(IntPtr obj, string sel, bool value) => SendBool(obj, Sel(sel), value);
    public static void SetDouble(IntPtr obj, string sel, double value) => SendDouble(obj, Sel(sel), value);
    public static void SetInt(IntPtr obj, string sel, nint value) => SendInt(obj, Sel(sel), value);

    private static readonly Dictionary<string, IntPtr> Selectors = new();

    public static IntPtr Sel(string name)
    {
        lock (Selectors)
        {
            if (!Selectors.TryGetValue(name, out var sel))
                Selectors[name] = sel = sel_registerName(name);
            return sel;
        }
    }

    public static IntPtr Cls(string name) => objc_getClass(name);

    /// <summary>Načte Foundation a AppKit – bez toho objc_getClass("NSApplication") vrací nil
    /// a všechny zprávy AppKitu tiše nic nedělají.</summary>
    public static bool LoadAppKit()
    {
        bool ok = true;
        foreach (var fw in new[]
        {
            "/System/Library/Frameworks/Foundation.framework/Foundation",
            "/System/Library/Frameworks/AppKit.framework/AppKit",
        })
        {
            if (!NativeLibrary.TryLoad(fw, out _))
            {
                Log.Error("Nelze načíst " + fw);
                ok = false;
            }
        }
        return ok && objc_getClass("NSApplication") != IntPtr.Zero && objc_getClass("NSStatusBar") != IntPtr.Zero;
    }

    /// <summary>NSString (autorelease) z .NET řetězce.</summary>
    public static IntPtr Str(string s)
    {
        IntPtr cf = Native.CFStringCreateWithCString(IntPtr.Zero, s, Native.kCFStringEncodingUTF8);
        return cf == IntPtr.Zero ? IntPtr.Zero : Send(cf, Sel("autorelease"));
    }

    /// <summary>[[Class alloc] init]</summary>
    public static IntPtr New(string cls) => Send(Send(Cls(cls), Sel("alloc")), Sel("init"));

    public static bool RespondsTo(IntPtr obj, string selector) =>
        obj != IntPtr.Zero && SendRetBool(obj, Sel("respondsToSelector:"), Sel(selector));
}
