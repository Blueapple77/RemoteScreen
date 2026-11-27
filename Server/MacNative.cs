using System.Runtime.InteropServices;

namespace RemoteScreen.Server;

[StructLayout(LayoutKind.Sequential)]
internal struct CGPoint
{
    public double X, Y;
    public CGPoint(double x, double y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGSize
{
    public double Width, Height;
    public CGSize(double w, double h) { Width = w; Height = h; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CGRect
{
    public CGPoint Origin;
    public CGSize Size;

    public CGRect(double x, double y, double w, double h)
    {
        Origin = new CGPoint(x, y);
        Size = new CGSize { Width = w, Height = h };
    }

    public double X => Origin.X;
    public double Y => Origin.Y;
    public double W => Size.Width;
    public double H => Size.Height;
    public bool Contains(CGPoint p) => p.X >= X && p.Y >= Y && p.X < X + W && p.Y < Y + H;
}

/// <summary>P/Invoke do systémových frameworků macOS (CoreGraphics, CoreFoundation, ImageIO, ApplicationServices).</summary>
internal static class Native
{
    public const string CoreGraphicsPath = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string CoreFoundationPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string ImageIOPath = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
    public const string AppServicesPath = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    public const string IOKitPath = "/System/Library/Frameworks/IOKit.framework/IOKit";

    /// <summary>Ohlásí „uživatel je u počítače“ – rozsvítí displej a odloží jeho uspání (totéž dělá caffeinate -u).</summary>
    [DllImport(IOKitPath)]
    public static extern int IOPMAssertionDeclareUserActivity(IntPtr assertionName, uint userType, ref uint assertionId);

    public const uint kCFStringEncodingUTF8 = 0x08000100;
    public const nint kCFNumberFloat64Type = 13;

    // ---------- CoreFoundation ----------
    [DllImport(CoreFoundationPath)] public static extern void CFRelease(IntPtr obj);
    [DllImport(CoreFoundationPath)] public static extern IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);
    [DllImport(CoreFoundationPath)] public static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundationPath)] public static extern IntPtr CFDataGetBytePtr(IntPtr data);

    [DllImport(CoreFoundationPath)]
    public static extern IntPtr CFStringCreateWithCString(IntPtr allocator,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string str, uint encoding);

    [DllImport(CoreFoundationPath)] public static extern IntPtr CFNumberCreate(IntPtr allocator, nint type, ref double value);
    [DllImport(CoreFoundationPath)] public static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundationPath)] public static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundationPath)] public static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);

    [DllImport(CoreFoundationPath)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CFNumberGetValue(IntPtr number, nint type, out int value);

    [DllImport(CoreFoundationPath)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CFStringGetCString(IntPtr str, byte[] buffer, nint bufferSize, uint encoding);

    public static string? CFStringToString(IntPtr str)
    {
        if (str == IntPtr.Zero) return null;
        var buf = new byte[4096];
        if (!CFStringGetCString(str, buf, buf.Length, kCFStringEncodingUTF8)) return null;
        int len = Array.IndexOf(buf, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
    }

    [DllImport(CoreFoundationPath)]
    public static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint count,
        IntPtr keyCallBacks, IntPtr valueCallBacks);

    // ---------- displeje a snímání ----------
    [DllImport(CoreGraphicsPath)] public static extern uint CGMainDisplayID();
    [DllImport(CoreGraphicsPath)] public static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[]? displays, out uint count);
    [DllImport(CoreGraphicsPath)] public static extern CGRect CGDisplayBounds(uint display);
    [DllImport(CoreGraphicsPath)] public static extern IntPtr CGDisplayCreateImage(uint display);
    [DllImport(CoreGraphicsPath)] public static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [DllImport(CoreGraphicsPath)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);
    [DllImport(CoreGraphicsPath)] public static extern nuint CGImageGetWidth(IntPtr image);
    [DllImport(CoreGraphicsPath)] public static extern nuint CGImageGetHeight(IntPtr image);
    [DllImport(CoreGraphicsPath)] public static extern void CGImageRelease(IntPtr image);
    [DllImport(CoreGraphicsPath)] public static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(CoreGraphicsPath)] public static extern void CGColorSpaceRelease(IntPtr cs);

    [DllImport(CoreGraphicsPath)]
    public static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent,
        nuint bytesPerRow, IntPtr colorSpace, uint bitmapInfo);

    [DllImport(CoreGraphicsPath)] public static extern void CGContextSetInterpolationQuality(IntPtr ctx, int quality);
    [DllImport(CoreGraphicsPath)] public static extern void CGContextDrawImage(IntPtr ctx, CGRect rect, IntPtr image);
    [DllImport(CoreGraphicsPath)] public static extern IntPtr CGBitmapContextCreateImage(IntPtr ctx);
    [DllImport(CoreGraphicsPath)] public static extern void CGContextRelease(IntPtr ctx);

    [DllImport(CoreGraphicsPath)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CoreGraphicsPath)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGRequestScreenCaptureAccess();

    // ---------- události (myš, klávesnice) ----------
    [DllImport(CoreGraphicsPath)] public static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint position, uint button);

    [DllImport(CoreGraphicsPath)]
    public static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.U1)] bool keyDown);

    // Nevariadická verze (macOS 10.13+), variadická funkce se na arm64 přes P/Invoke volat nedá.
    [DllImport(CoreGraphicsPath)]
    public static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2, int wheel3);

    [DllImport(CoreGraphicsPath)] public static extern void CGEventSetFlags(IntPtr ev, ulong flags);
    [DllImport(CoreGraphicsPath)] public static extern void CGEventSetType(IntPtr ev, uint type);
    [DllImport(CoreGraphicsPath)] public static extern void CGEventSetIntegerValueField(IntPtr ev, uint field, long value);
    [DllImport(CoreGraphicsPath)] public static extern void CGEventKeyboardSetUnicodeString(IntPtr ev, nuint length, ushort[] chars);
    [DllImport(CoreGraphicsPath)] public static extern void CGEventPost(uint tap, IntPtr ev);

    // ---------- ImageIO ----------
    [DllImport(ImageIOPath)] public static extern IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr type, nuint count, IntPtr options);
    [DllImport(ImageIOPath)] public static extern void CGImageDestinationAddImage(IntPtr dest, IntPtr image, IntPtr properties);
    [DllImport(ImageIOPath)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool CGImageDestinationFinalize(IntPtr dest);

    // ---------- Accessibility ----------
    [DllImport(AppServicesPath)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXIsProcessTrusted();
    [DllImport(AppServicesPath)] [return: MarshalAs(UnmanagedType.U1)] public static extern bool AXIsProcessTrustedWithOptions(IntPtr options);

    // ---------- exportované konstanty (datové symboly) ----------
    private static readonly Lazy<IntPtr> CfLib = new(() => NativeLibrary.Load(CoreFoundationPath));
    private static readonly Lazy<IntPtr> ImageIOLib = new(() => NativeLibrary.Load(ImageIOPath));
    private static readonly Lazy<IntPtr> AppServicesLib = new(() => NativeLibrary.Load(AppServicesPath));
    public static readonly Lazy<IntPtr> CoreGraphicsLib = new(() => NativeLibrary.Load(CoreGraphicsPath));

    public static IntPtr DictKeyCallBacks => NativeLibrary.GetExport(CfLib.Value, "kCFTypeDictionaryKeyCallBacks");
    public static IntPtr DictValueCallBacks => NativeLibrary.GetExport(CfLib.Value, "kCFTypeDictionaryValueCallBacks");

    /// <summary>Hodnota globální proměnné typu CFTypeRef (např. CFStringRef konstanta).</summary>
    public static IntPtr ReadConstant(IntPtr lib, string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, name));

    public static IntPtr CFBooleanTrue => ReadConstant(CfLib.Value, "kCFBooleanTrue");
    public static IntPtr JpegQualityKey => ReadConstant(ImageIOLib.Value, "kCGImageDestinationLossyCompressionQuality");
    public static IntPtr AXTrustedCheckOptionPrompt => ReadConstant(AppServicesLib.Value, "kAXTrustedCheckOptionPrompt");

    public static IntPtr CreateDictionary(IntPtr key, IntPtr value) =>
        CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { value }, 1, DictKeyCallBacks, DictValueCallBacks);
}

/// <summary>Kódy událostí CoreGraphics.</summary>
internal static class CGConst
{
    public const uint LeftMouseDown = 1, LeftMouseUp = 2, RightMouseDown = 3, RightMouseUp = 4, MouseMoved = 5,
        LeftMouseDragged = 6, RightMouseDragged = 7, OtherMouseDown = 25, OtherMouseUp = 26, OtherMouseDragged = 27;

    public const uint EventFlagsChanged = 12;

    public const uint ButtonLeft = 0, ButtonRight = 1, ButtonCenter = 2;

    public const uint HIDEventTap = 0;
    public const uint ScrollUnitPixel = 0;

    public const uint FieldMouseClickState = 1;
    public const uint FieldKeyboardAutorepeat = 8;

    public const ulong FlagAlphaShift = 0x00010000, FlagShift = 0x00020000, FlagControl = 0x00040000,
        FlagAlternate = 0x00080000, FlagCommand = 0x00100000, FlagNonCoalesced = 0x00000100;
}
