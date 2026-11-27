using System.Runtime.InteropServices;

namespace RemoteScreen.Server;

/// <summary>Dekódování JPEG snímků (ImageIO) pro okno se vzdálenou plochou.</summary>
internal static class ClientNative
{
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";

    [DllImport(CF)] private static extern unsafe IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);
    [DllImport(ImageIO)] private static extern IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);
    [DllImport(ImageIO)] private static extern IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nuint index, IntPtr options);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern long CGEventGetIntegerValueField(IntPtr ev, uint field);

    private static readonly long OwnPid = Environment.ProcessId;

    /// <summary>Událost vložil RemoteScreen sám (tento Mac je zrovna ovládaný na dálku).
    /// Takové události okno klienta nepřeposílá – jinak by se myš zacyklila.</summary>
    public static bool IsRemoteInjected(IntPtr nsEvent)
    {
        IntPtr cg = ObjC.Send(nsEvent, ObjC.Sel("CGEvent"));
        if (cg == IntPtr.Zero) return false;
        return CGEventGetIntegerValueField(cg, InputInjector.FieldSourceUserData) == InputInjector.InjectedMarker ||
               CGEventGetIntegerValueField(cg, InputInjector.FieldSourcePid) == OwnPid;
    }

    private static IntPtr _decodeOptions;

    /// <summary>JPEG → CGImage (+1 reference, uvolnit přes CGImageRelease). Dekóduje hned,
    /// aby se to nedělalo až při vykreslení na hlavním vlákně.</summary>
    public static unsafe IntPtr DecodeJpeg(byte[] jpeg)
    {
        if (_decodeOptions == IntPtr.Zero)
        {
            IntPtr pool = ObjC.objc_autoreleasePoolPush();
            IntPtr yes = ObjC.SendBool(ObjC.Cls("NSNumber"), ObjC.Sel("numberWithBool:"), true);
            IntPtr dict = ObjC.Send(ObjC.Cls("NSDictionary"), ObjC.Sel("dictionaryWithObject:forKey:"),
                yes, ObjC.Str("kCGImageSourceShouldCacheImmediately"));
            _decodeOptions = ObjC.Send(dict, ObjC.Sel("retain"));
            ObjC.objc_autoreleasePoolPop(pool);
        }

        IntPtr data;
        fixed (byte* p = jpeg) data = CFDataCreate(IntPtr.Zero, p, jpeg.Length);
        if (data == IntPtr.Zero) return IntPtr.Zero;
        IntPtr src = CGImageSourceCreateWithData(data, IntPtr.Zero);
        Native.CFRelease(data);
        if (src == IntPtr.Zero) return IntPtr.Zero;
        IntPtr img = CGImageSourceCreateImageAtIndex(src, 0, _decodeOptions);
        Native.CFRelease(src);
        return img;
    }

    /// <summary>NSData z bajtů (autorelease).</summary>
    public static unsafe IntPtr NSData(byte[] bytes)
    {
        fixed (byte* p = bytes)
            return ObjC.SendObjInt(ObjC.Cls("NSData"), ObjC.Sel("dataWithBytes:length:"), (IntPtr)p, bytes.Length);
    }
}
