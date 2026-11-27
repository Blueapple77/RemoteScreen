using System.Runtime.InteropServices;

namespace RemoteScreen.Server;

/// <summary>Aktuální tvar kurzoru na Macu (NSCursor.currentSystemCursor) – snímek obrazovky kurzor neobsahuje.</summary>
internal static class CursorCapture
{
    private static bool _disabled;

    /// <summary>Vrátí nový tvar, nebo null, pokud se od posledně nezměnil (podle otisku obrázku).</summary>
    public static CursorShapeData? GetIfChanged(ref int lastHash)
    {
        if (_disabled || !ObjC.LoadAppKit()) return null;
        IntPtr pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            IntPtr cursor = ObjC.Send(ObjC.Cls("NSCursor"), ObjC.Sel("currentSystemCursor"));
            if (cursor == IntPtr.Zero) return null;
            IntPtr image = ObjC.Send(cursor, ObjC.Sel("image"));
            if (image == IntPtr.Zero) return null;
            IntPtr tiff = ObjC.Send(image, ObjC.Sel("TIFFRepresentation"));
            if (tiff == IntPtr.Zero) return null;

            var hot = ObjC.SendRetPoint(cursor, ObjC.Sel("hotSpot"));
            var size = ObjC.SendRetSize(image, ObjC.Sel("size"));

            byte[] tiffBytes = CopyData(tiff);
            var hc = new HashCode();
            hc.AddBytes(tiffBytes);
            hc.Add(hot.X);
            hc.Add(hot.Y);
            int hash = hc.ToHashCode();
            if (hash == lastHash) return null;

            IntPtr rep = ObjC.Send(ObjC.Cls("NSBitmapImageRep"), ObjC.Sel("imageRepWithData:"), tiff);
            if (rep == IntPtr.Zero) return null;
            IntPtr props = ObjC.Send(ObjC.Cls("NSDictionary"), ObjC.Sel("dictionary"));
            IntPtr png = ObjC.SendIntObj(rep, ObjC.Sel("representationUsingType:properties:"), 4 /* PNG */, props);
            if (png == IntPtr.Zero) return null;

            lastHash = hash;
            return new CursorShapeData(CopyData(png), (float)hot.X, (float)hot.Y, (float)size.Width, (float)size.Height);
        }
        catch (Exception ex)
        {
            _disabled = true; // nefunguje-li to jednou, nezkoušet při každém snímku
            Log.Warn("Tvar kurzoru nelze zjistit: " + ex.Message);
            return null;
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private static byte[] CopyData(IntPtr nsData)
    {
        int len = (int)ObjC.SendRetInt(nsData, ObjC.Sel("length"));
        var bytes = new byte[len];
        if (len > 0) Marshal.Copy(ObjC.Send(nsData, ObjC.Sel("bytes")), bytes, 0, len);
        return bytes;
    }
}
