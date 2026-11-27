using System.Runtime.InteropServices;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Soubory, které uživatel právě táhne na Macu (schránka tažení – NSPasteboardNameDrag).</summary>
internal static class DragPasteboard
{
    private static IntPtr Board() =>
        ObjC.Send(ObjC.Cls("NSPasteboard"), ObjC.Sel("pasteboardWithName:"), ObjC.Str("Apple CFPasteboard drag"));

    /// <summary>Počítadlo změn schránky tažení – zvýší se, když na Macu začne nové tažení.</summary>
    public static long ChangeCount()
    {
        if (!ObjC.LoadAppKit()) return -1;
        IntPtr pool = ObjC.objc_autoreleasePoolPush();
        try { return ObjC.SendRetInt(Board(), ObjC.Sel("changeCount")); }
        catch { return -1; }
        finally { ObjC.objc_autoreleasePoolPop(pool); }
    }

    /// <summary>Cesty tažených souborů/složek (převádí i odkazy file:///.file/id=… z Finderu).</summary>
    public static List<string> FilePaths()
    {
        var result = new List<string>();
        if (!ObjC.LoadAppKit()) return result;
        IntPtr pool = ObjC.objc_autoreleasePoolPush();
        try
        {
            IntPtr items = ObjC.Send(Board(), ObjC.Sel("pasteboardItems"));
            if (items == IntPtr.Zero) return result;
            nint n = ObjC.SendRetInt(items, ObjC.Sel("count"));
            for (nint i = 0; i < n; i++)
            {
                IntPtr item = ObjC.SendInt(items, ObjC.Sel("objectAtIndex:"), i);
                IntPtr urlString = ObjC.Send(item, ObjC.Sel("stringForType:"), ObjC.Str("public.file-url"));
                if (urlString == IntPtr.Zero) continue;
                IntPtr url = ObjC.Send(ObjC.Cls("NSURL"), ObjC.Sel("URLWithString:"), urlString);
                if (url == IntPtr.Zero) continue;
                IntPtr fileUrl = ObjC.Send(url, ObjC.Sel("filePathURL")); // vyřeší odkaz .file/id=…
                IntPtr path = fileUrl != IntPtr.Zero ? ObjC.Send(fileUrl, ObjC.Sel("path")) : IntPtr.Zero;
                string? s = Native.CFStringToString(path);
                if (!string.IsNullOrEmpty(s) && (File.Exists(s) || Directory.Exists(s))) result.Add(s);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Čtení tažených souborů selhalo: " + ex.Message);
        }
        finally
        {
            ObjC.objc_autoreleasePoolPop(pool);
        }
        return result;
    }

    /// <summary>Rozbalí tažené položky na seznam souborů s relativními cestami (složky rekurzivně).</summary>
    public static DragFiles Describe(List<string> paths)
    {
        var items = new List<DragItem>();
        var roots = new List<string>();
        foreach (var raw in paths)
        {
            string p = raw.TrimEnd('/');
            string name = Path.GetFileName(p);
            if (name.Length == 0) continue;
            try
            {
                if (File.Exists(p))
                {
                    roots.Add(name);
                    items.Add(new DragItem(p, name, new FileInfo(p).Length));
                }
                else if (Directory.Exists(p))
                {
                    roots.Add(name);
                    foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        if (Path.GetFileName(f) == ".DS_Store") continue;
                        items.Add(new DragItem(f, name + "/" + Path.GetRelativePath(p, f), new FileInfo(f).Length));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Tažená položka {p}: {ex.Message}");
            }
        }
        return new DragFiles(items.ToArray(), roots.ToArray());
    }
}
