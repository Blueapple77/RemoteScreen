using System.Runtime.InteropServices;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Vlastní NSView „RSRemoteView“: myš, kolečko, klávesnice, kurzor a přetahování souborů.</summary>
internal sealed partial class RemoteWindow
{
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern IntPtr objc_getProtocol([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);

    private static IntPtr _viewClass;

    private static unsafe IntPtr RemoteViewClass()
    {
        if (_viewClass != IntPtr.Zero) return _viewClass;
        IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSView"), "RSRemoteView", 0);

        void Ev(string sel, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> f) => ObjC.class_addMethod(cls, ObjC.Sel(sel), (IntPtr)f, "v@:@");
        Ev("mouseDown:", &OnMouseDown);
        Ev("mouseUp:", &OnMouseUp);
        Ev("rightMouseDown:", &OnRightDown);
        Ev("rightMouseUp:", &OnRightUp);
        Ev("otherMouseDown:", &OnOtherDown);
        Ev("otherMouseUp:", &OnOtherUp);
        Ev("mouseMoved:", &OnMouseMoved);
        Ev("mouseDragged:", &OnMouseDragged);
        Ev("rightMouseDragged:", &OnMouseMoved);
        Ev("otherMouseDragged:", &OnMouseMoved);
        Ev("scrollWheel:", &OnScroll);
        Ev("keyDown:", &OnKeyDown);
        Ev("keyUp:", &OnKeyUp);
        Ev("flagsChanged:", &OnFlagsChanged);
        Ev("cursorUpdate:", &OnCursorUpdate);
        Ev("draggingExited:", &OnDraggingExited);

        ObjC.class_addMethod(cls, ObjC.Sel("acceptsFirstResponder"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&Yes, "c@:");
        ObjC.class_addMethod(cls, ObjC.Sel("wantsUpdateLayer"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&Yes, "c@:");
        ObjC.class_addMethod(cls, ObjC.Sel("acceptsFirstMouse:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&YesArg, "c@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("prepareForDragOperation:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&YesArg, "c@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("performKeyEquivalent:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&OnKeyEquivalent, "c@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("performDragOperation:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&OnPerformDrag, "c@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("updateLayer"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnUpdateLayer, "v@:");
        ObjC.class_addMethod(cls, ObjC.Sel("draggingEntered:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)&OnDraggingEntered, "Q@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("draggingUpdated:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)&OnDraggingEntered, "Q@:@");
        // NSDraggingSource – tažení souborů z druhého počítače ven do Finderu
        ObjC.class_addMethod(cls, ObjC.Sel("draggingSession:sourceOperationMaskForDraggingContext:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nint, nuint>)&OnSourceMask, "Q@:@q");
        ObjC.class_addMethod(cls, ObjC.Sel("draggingSession:endedAtPoint:operation:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, CGPoint, nuint, void>)&OnDragEnded, "v@:@{CGPoint=dd}Q");
        IntPtr proto = objc_getProtocol("NSDraggingSource");
        if (proto != IntPtr.Zero) class_addProtocol(cls, proto);

        ObjC.objc_registerClassPair(cls);
        return _viewClass = cls;
    }

    /// <summary>Jako <see cref="With"/>, ale ignoruje vstup vložený ovládáním na dálku (ochrana proti smyčce).</summary>
    private static void WithInput(IntPtr view, IntPtr ev, Action<RemoteWindow> a) => With(view, w =>
    {
        if (w.IsLoopInput(ev)) return;
        a(w);
    });

    /// <summary>Událost vložil RemoteScreen, protože tento Mac právě ovládá počítač, který je v okně →
    /// nepřeposílat zpět, jinak se myš zacyklí. (Ovládat přes tento Mac jiný, třetí počítač jde.)</summary>
    private bool IsLoopInput(IntPtr ev)
    {
        try
        {
            return ClientNative.IsRemoteInjected(ev) && ServerHost.IsControlledFrom(_client.RemoteAddress);
        }
        catch { return false; }
    }

    private static void With(IntPtr view, Action<RemoteWindow> a)
    {
        try
        {
            if (ByView.TryGetValue(view, out var w) && !w._closed) a(w);
        }
        catch (Exception ex) { Log.Error("Vzdálená plocha: " + ex); }
    }

    [UnmanagedCallersOnly] private static byte Yes(IntPtr self, IntPtr sel) => 1;
    [UnmanagedCallersOnly] private static byte YesArg(IntPtr self, IntPtr sel, IntPtr arg) => 1;

    [UnmanagedCallersOnly]
    private static void OnUpdateLayer(IntPtr self, IntPtr sel) => With(self, w =>
    {
        IntPtr layer = ObjC.Get(self, "layer");
        if (layer == IntPtr.Zero) return;
        ObjC.Set(layer, "setContentsGravity:", ObjC.Str("resizeAspect"));
        ObjC.Set(layer, "setContents:", w._image);
    });

    // ---------------- myš ----------------

    [UnmanagedCallersOnly] private static void OnMouseDown(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseButton(ev, 0, true));
    [UnmanagedCallersOnly] private static void OnMouseUp(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseButton(ev, 0, false));
    [UnmanagedCallersOnly] private static void OnRightDown(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseButton(ev, 1, true));
    [UnmanagedCallersOnly] private static void OnRightUp(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseButton(ev, 1, false));
    [UnmanagedCallersOnly] private static void OnOtherDown(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseButton(ev, 2, true));
    [UnmanagedCallersOnly] private static void OnOtherUp(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseButton(ev, 2, false));
    [UnmanagedCallersOnly] private static void OnMouseMoved(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseMove(ev, dragging: false));
    [UnmanagedCallersOnly] private static void OnMouseDragged(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.MouseMove(ev, dragging: true));
    [UnmanagedCallersOnly] private static void OnScroll(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.Scroll(ev));
    [UnmanagedCallersOnly] private static void OnCursorUpdate(IntPtr self, IntPtr sel, IntPtr ev) => With(self, w => w.UpdateLocalCursor(w.LocalPoint(ev)));

    private void MouseButton(IntPtr ev, byte button, bool down)
    {
        var p = LocalPoint(ev);
        var (x, y, _) = Normalize(p);
        if (down)
        {
            ObjC.Send(_window, ObjC.Sel("makeFirstResponder:"), _view);
            _lastX = x; _lastY = y;
            _client.Post(MsgType.MouseMove, Bin.MouseMove(x, y));
        }
        if (button == 0)
        {
            if (down)
            {
                _leftDown = true;
                _dragOutTried = false;
                _suppressLeftUp = false;
            }
            else
            {
                _leftDown = false;
                if (_suppressLeftUp) { _suppressLeftUp = false; return; } // tlačítko už bylo puštěné při tažení ven
            }
        }
        byte clicks = (byte)Math.Clamp(ObjC.SendRetInt(ev, ObjC.Sel("clickCount")), 1, 3);
        _client.Post(MsgType.MouseButton, Bin.MouseButton(button, down, clicks, x, y));
    }

    private void MouseMove(IntPtr ev, bool dragging)
    {
        var p = LocalPoint(ev);
        if (!dragging)
        {
            UpdateLocalCursor(p);
            if (OverDock(p)) { UpdateMonitorHover(p); return; }
            UpdateMonitorHover(null);
        }
        var (x, y, inside) = Normalize(p);

        if (dragging && _leftDown && !inside && !_dragOutTried)
        {
            // myš opustila obraz se stisknutým tlačítkem → možná se na druhém počítači táhnou soubory
            _dragOutTried = true;
            IntPtr retained = ObjC.Send(ev, ObjC.Sel("retain"));
            _ = DragOutAsync(retained);
        }

        if (Math.Abs(x - _lastX) < 0.00001f && Math.Abs(y - _lastY) < 0.00001f) return;
        _lastX = x; _lastY = y;
        _client.Post(MsgType.MouseMove, Bin.MouseMove(x, y));
    }

    private void Scroll(IntPtr ev)
    {
        double dy = ObjC.SendRetDouble(ev, ObjC.Sel("scrollingDeltaY"));
        double dx = ObjC.SendRetDouble(ev, ObjC.Sel("scrollingDeltaX"));
        bool precise = ObjC.SendRetBool0(ev, ObjC.Sel("hasPreciseScrollingDeltas"));
        // protokol: 120 = jeden zub kolečka (Mac server: 40 px); trackpad posílá pixely
        double k = precise ? 3 : 120;
        _scrollAccY += dy * k;
        _scrollAccX += dx * k;
        short sy = (short)Math.Clamp(Math.Truncate(_scrollAccY), short.MinValue, short.MaxValue);
        short sx = (short)Math.Clamp(Math.Truncate(_scrollAccX), short.MinValue, short.MaxValue);
        _scrollAccY -= sy;
        _scrollAccX -= sx;
        if (_targetWindows && !precise)
        {
            // Windows: celé „zuby“ (120), jinak některé aplikace drobné kroky ignorují
            if (Math.Abs(sy) < 1 && Math.Abs(sx) < 1) return;
        }
        if (sy != 0 || sx != 0) _client.Post(MsgType.MouseScroll, Bin.Scroll(sy, sx));
    }

    // ---------------- klávesnice ----------------

    [UnmanagedCallersOnly] private static void OnKeyDown(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.Key(ev, true));
    [UnmanagedCallersOnly] private static void OnKeyUp(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.Key(ev, false));
    [UnmanagedCallersOnly] private static void OnFlagsChanged(IntPtr self, IntPtr sel, IntPtr ev) => WithInput(self, ev, w => w.Flags(ev));

    /// <summary>Zkratky s Cmd (Cmd+Q, Cmd+W, Cmd+C…) jdou na druhý počítač, ne do této aplikace.</summary>
    [UnmanagedCallersOnly]
    private static byte OnKeyEquivalent(IntPtr self, IntPtr sel, IntPtr ev)
    {
        byte handled = 0;
        With(self, w =>
        {
            if (w.IsLoopInput(ev)) return;
            if (!w._sendKeys) return;
            if (ObjC.SendRetUInt(ev, ObjC.Sel("type")) != 10 /* keyDown */) return;
            if (!ObjC.SendRetBool0(w._window, ObjC.Sel("isKeyWindow"))) return;
            if (ObjC.Get(w._window, "firstResponder") != self) return;
            ushort code = ObjC.SendRetUShort(ev, ObjC.Sel("keyCode"));
            ushort mapped = w.Map(code);
            // macOS u zkratek s Cmd neposílá keyUp → pustit hned
            w._client.Post(MsgType.Key, Bin.Key(mapped, true));
            w._client.Post(MsgType.Key, Bin.Key(mapped, false));
            w._autoReleased.Add(code);
            handled = 1;
        });
        return handled;
    }

    private void Key(IntPtr ev, bool down)
    {
        if (!_sendKeys) return;
        ushort code = ObjC.SendRetUShort(ev, ObjC.Sel("keyCode"));
        if (!down && _autoReleased.Remove(code)) return;
        ushort mapped = Map(code);
        if (down) _keysDown.Add(mapped); else _keysDown.Remove(mapped);
        _client.Post(MsgType.Key, Bin.Key(mapped, down));
    }

    private void Flags(IntPtr ev)
    {
        if (!_sendKeys) return;
        ushort code = ObjC.SendRetUShort(ev, ObjC.Sel("keyCode"));
        ulong flags = ObjC.SendRetUInt(ev, ObjC.Sel("modifierFlags"));
        if (code == 0x39) // Caps Lock – přepnout
        {
            _client.Post(MsgType.Key, Bin.Key(0x39, true));
            _client.Post(MsgType.Key, Bin.Key(0x39, false));
            return;
        }
        // (bit levé klávesy, bit pravé klávesy, obecný bit, je to pravá?)
        (ulong L, ulong R, ulong Generic, bool Right)? m = code switch
        {
            0x38 => (0x2UL, 0x4UL, 1UL << 17, false),       // Shift
            0x3C => (0x2UL, 0x4UL, 1UL << 17, true),
            0x3B => (0x1UL, 0x2000UL, 1UL << 18, false),    // Control
            0x3E => (0x1UL, 0x2000UL, 1UL << 18, true),
            0x3A => (0x20UL, 0x40UL, 1UL << 19, false),     // Option
            0x3D => (0x20UL, 0x40UL, 1UL << 19, true),
            0x37 => (0x8UL, 0x10UL, 1UL << 20, false),      // Command
            0x36 => (0x8UL, 0x10UL, 1UL << 20, true),
            _ => null,
        };
        if (m is not { } k) return;
        bool deviceBits = (flags & (k.L | k.R)) != 0;
        bool down = deviceBits ? (flags & (k.Right ? k.R : k.L)) != 0 : (flags & k.Generic) != 0;
        ushort mapped = Map(code);
        if (down == _keysDown.Contains(mapped)) return;
        if (down) _keysDown.Add(mapped); else _keysDown.Remove(mapped);
        _client.Post(MsgType.Key, Bin.Key(mapped, down));
    }

    /// <summary>Při ovládání Windows: Cmd ↔ Ctrl (Cmd+C = kopírovat). Kódy jsou macOS kVK.</summary>
    private ushort Map(ushort code)
    {
        if (!_targetWindows || !_settings.SwapCmdCtrl) return code;
        return code switch
        {
            0x37 => 0x3B, // levý Cmd → levý Ctrl
            0x36 => 0x3E, // pravý Cmd → pravý Ctrl
            0x3B => 0x37, // levý Ctrl → klávesa Windows
            0x3E => 0x36,
            _ => code,
        };
    }

    private void ReleaseKeys()
    {
        if (_closed && !_client.IsConnected) return;
        foreach (var k in _keysDown) _client.Post(MsgType.Key, Bin.Key(k, false));
        _keysDown.Clear();
        _autoReleased.Clear();
        _client.Post(MsgType.ReleaseAll);
    }

    // ---------------- přetažení souborů do okna (Mac → druhý počítač) ----------------

    [UnmanagedCallersOnly]
    private static nuint OnDraggingEntered(IntPtr self, IntPtr sel, IntPtr info)
    {
        nuint op = 0;
        With(self, w =>
        {
            if (ObjC.Get(info, "draggingSource") == self) return; // vlastní tažení ven
            op = 1; // kopírovat
        });
        return op;
    }

    [UnmanagedCallersOnly]
    private static void OnDraggingExited(IntPtr self, IntPtr sel, IntPtr info) { }

    [UnmanagedCallersOnly]
    private static byte OnPerformDrag(IntPtr self, IntPtr sel, IntPtr info)
    {
        byte ok = 0;
        With(self, w =>
        {
            if (ObjC.Get(info, "draggingSource") == self) return;
            var paths = FileUrls(ObjC.Get(info, "draggingPasteboard"));
            if (paths.Count == 0) return;
            var p = ObjC.SendPointObjRetPoint(self, ObjC.Sel("convertPoint:fromView:"),
                ObjC.SendRetPoint(info, ObjC.Sel("draggingLocation")), IntPtr.Zero);
            var (x, y, inside) = w.Normalize(p);
            _ = w.UploadAsync(paths, inside ? x : -1, inside ? y : -1);
            ok = 1;
        });
        return ok;
    }

    private static List<string> FileUrls(IntPtr pasteboard)
    {
        var result = new List<string>();
        IntPtr classes = ObjC.Send(ObjC.Cls("NSArray"), ObjC.Sel("arrayWithObject:"), ObjC.Cls("NSURL"));
        IntPtr urls = ObjC.Send(pasteboard, ObjC.Sel("readObjectsForClasses:options:"), classes, IntPtr.Zero);
        if (urls == IntPtr.Zero) return result;
        nuint n = ObjC.SendRetUInt(urls, ObjC.Sel("count"));
        for (nuint i = 0; i < n; i++)
        {
            IntPtr url = ObjC.SendRetObjInt(urls, ObjC.Sel("objectAtIndex:"), i);
            if (!ObjC.SendRetBool0(url, ObjC.Sel("isFileURL"))) continue;
            string? path = ObjC.ToStr(ObjC.Get(url, "path"));
            if (path != null) result.Add(path);
        }
        return result;
    }

    private void UploadViaPanel()
    {
        IntPtr panel = ObjC.Get(ObjC.Cls("NSOpenPanel"), "openPanel");
        ObjC.SetBool(panel, "setCanChooseFiles:", true);
        ObjC.SetBool(panel, "setCanChooseDirectories:", true);
        ObjC.SetBool(panel, "setAllowsMultipleSelection:", true);
        ObjC.Set(panel, "setMessage:", ObjC.Str(_targetWindows
            ? "Soubory se uloží do otevřené složky Průzkumníka, jinak na Plochu."
            : "Soubory se uloží do otevřené složky Finderu, jinak na Plochu."));
        ObjC.Set(panel, "setPrompt:", ObjC.Str("Nahrát"));
        ReleaseKeys();
        if (ObjC.SendRetInt(panel, ObjC.Sel("runModal")) != 1) return;
        IntPtr urls = ObjC.Get(panel, "URLs");
        var paths = new List<string>();
        nuint n = ObjC.SendRetUInt(urls, ObjC.Sel("count"));
        for (nuint i = 0; i < n; i++)
        {
            string? p = ObjC.ToStr(ObjC.Get(ObjC.SendRetObjInt(urls, ObjC.Sel("objectAtIndex:"), i), "path"));
            if (p != null) paths.Add(p);
        }
        if (paths.Count > 0) _ = UploadAsync(paths, -1, -1);
    }

    /// <summary>Nahraje soubory tam, kde byly puštěny (složka Finderu/Průzkumníka, aplikace), jinak na Plochu.</summary>
    private async Task UploadAsync(List<string> paths, float x, float y)
    {
        string what = FileTransfers.Describe(paths);
        Toast($"⇡ {what} – připravuji…", sticky: true, progress: 0);
        var target = await GetDropTargetAsync(x, y);
        FileTransfers.UploadResult result;
        try
        {
            result = await _files.UploadAsync(paths, target.Path, overwrite: false, (text, pct) =>
            {
                Toast($"⇡ {what} – {text}", sticky: true, progress: pct);
                SetTransfer($"⇡ {what} {pct} %");
            });
        }
        finally
        {
            SetTransfer(null);
        }
        if (result.Error != null)
        {
            Toast(result.Error, error: true);
        }
        else if (result.Files > 0 && target.OpenWithApp != null)
        {
            // skutečné cesty – soubor mohl dostat číslo („report 2.pdf“), pokud už stejný existoval
            RunAction("open_with", target.OpenWithApp + "\n" + string.Join("\n", result.RemoteRoots));
            Toast($"Hotovo – otevírám v: {target.Label.Replace("aplikace ", "")}");
        }
        else if (result.Files > 0)
        {
            Toast($"Hotovo – {what} → {target.Label}"); // co uživatel přetáhl, ne počet souborů uvnitř složek
        }
        else
        {
            Toast("Nebylo co nahrát.");
        }
    }

    private async Task<DropTarget> GetDropTargetAsync(float x, float y)
    {
        var tcs = new TaskCompletionSource<DropTarget>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _dropTargetWaiter, tcs)?.TrySetCanceled();
        _client.PostJson(MsgType.DropTargetGet, new DropQuery(x, y));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(20000));
        if (done == tcs.Task && tcs.Task.IsCompletedSuccessfully) return tcs.Task.Result;
        Interlocked.CompareExchange(ref _dropTargetWaiter, null, tcs);
        string home = _client.Info.HomeDir ?? "~";
        return new DropTarget(_files.Combine(home, "Desktop"), "Plocha");
    }

    // ---------------- tažení souborů ven z okna (druhý počítač → Mac) ----------------

    private static string DragTempRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "RemoteScreen", "drag");

    private string[]? _dragRoots;
    private string? _dragWhat;

    private async Task DragOutAsync(IntPtr dragEvent)
    {
        try
        {
            var tcs = new TaskCompletionSource<DragFiles>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _dragFilesWaiter, tcs)?.TrySetCanceled();
            _client.Post(MsgType.DragQuery);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
            Interlocked.CompareExchange(ref _dragFilesWaiter, null, tcs);
            if (done != tcs.Task || !tcs.Task.IsCompletedSuccessfully) return;
            var info = tcs.Task.Result;
            if (info.Items.Length == 0) return; // netáhne se soubor (např. okno nebo text)

            // zrušit tažení na druhém počítači (Esc) a pustit tam tlačítko
            _client.Post(MsgType.Key, Bin.Key(0x35, true));
            _client.Post(MsgType.Key, Bin.Key(0x35, false));
            _client.Post(MsgType.MouseButton, Bin.MouseButton(0, false, 1, _lastX, _lastY));
            MenuBarApp.RunOnMain(() => _suppressLeftUp = _leftDown);

            string what = FileTransfers.Describe(info.Roots);
            long total = info.Items.Sum(i => i.Size);
            Toast($"⇣ {what} – připravuji…", sticky: true, progress: 0);
            SetTransfer($"⇣ {what} 0 %");

            string temp = Path.Combine(DragTempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(temp);

            // Kde uživatel pustil tlačítko, pokud to udělal dřív, než se soubory stáhly (první přenos,
            // velké soubory). Pak je uložíme do okna Finderu / na Plochu pod tím místem.
            CGPoint? releasedAt = null;
            using var watchCts = new CancellationTokenSource();
            var watch = Task.Run(async () =>
            {
                while (!watchCts.IsCancellationRequested && releasedAt == null)
                {
                    if (!LeftPressed()) { releasedAt = GlobalMouse(); break; }
                    try { await Task.Delay(30, watchCts.Token); } catch (OperationCanceledException) { break; }
                }
            });

            string? error = await _files.DownloadAsync(info.Items, temp, (d, t) =>
            {
                long pct = t > 0 ? d * 100 / t : 100;
                Toast($"⇣ {what} – {FileTransfers.ProgressText(d, total)}", sticky: true, progress: (int)pct);
                SetTransfer($"⇣ {what} {pct} %");
            });
            watchCts.Cancel();
            await watch;
            if (releasedAt == null && !LeftPressed()) releasedAt = GlobalMouse();
            if (error != null)
            {
                Toast("Přenos selhal: " + error, error: true);
                return;
            }
            var roots = info.Roots.Select(r => Path.Combine(temp, r)).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (roots.Length == 0) { HideToast(); return; }

            if (releasedAt is CGPoint rp)
            {
                // tlačítko už je puštěné → uložit tam, kde bylo puštěno
                string? folder = await LocalFolderAtAsync(rp);
                MenuBarApp.RunOnMain(() => { if (!_closed) SaveTo(roots, what, folder); });
                return;
            }

            MenuBarApp.RunOnMain(() =>
            {
                if (_closed) return;
                if (LeftPressed() && BeginLocalDrag(roots, dragEvent))
                {
                    _dragRoots = roots;
                    _dragWhat = what;
                    Toast("Pusťte soubory, kam potřebujete (Finder, plocha, aplikace…)", sticky: true);
                }
                else
                {
                    SaveToDownloads(roots, what);
                }
            });
        }
        catch (Exception ex)
        {
            Toast("Přenos selhal: " + ex.Message, error: true);
        }
        finally
        {
            SetTransfer(null);
            MenuBarApp.RunOnMain(() => ObjC.Send(dragEvent, ObjC.Sel("release")));
        }
    }

    /// <summary>Spustí běžné tažení souborů v macOS (tlačítko je pořád stisknuté).</summary>
    private bool BeginLocalDrag(string[] roots, IntPtr dragEvent)
    {
        try
        {
            IntPtr items = ObjC.Get(ObjC.Cls("NSMutableArray"), "array");
            IntPtr ws = ObjC.Get(ObjC.Cls("NSWorkspace"), "sharedWorkspace");
            var p = LocalPoint(dragEvent);
            int i = 0;
            foreach (var path in roots)
            {
                IntPtr url = ObjC.Send(ObjC.Cls("NSURL"), ObjC.Sel("fileURLWithPath:"), ObjC.Str(path));
                IntPtr item = ObjC.Send(ObjC.Send(ObjC.Cls("NSDraggingItem"), ObjC.Sel("alloc")), ObjC.Sel("initWithPasteboardWriter:"), url);
                IntPtr icon = ObjC.Send(ws, ObjC.Sel("iconForFile:"), ObjC.Str(path));
                ObjC.SendRectObj(item, ObjC.Sel("setDraggingFrame:contents:"), new CGRect(p.X - 24 + i * 6, p.Y - 24 - i * 6, 48, 48), icon);
                ObjC.Send(items, ObjC.Sel("addObject:"), item);
                ObjC.Send(item, ObjC.Sel("release"));
                i++;
            }
            IntPtr session = ObjC.Send(_view, ObjC.Sel("beginDraggingSessionWithItems:event:source:"), items, dragEvent, _view);
            return session != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log.Warn("Tažení do Finderu: " + ex.Message);
            return false;
        }
    }

    [UnmanagedCallersOnly]
    private static nuint OnSourceMask(IntPtr self, IntPtr sel, IntPtr session, nint context) => 1; // kopírovat

    [UnmanagedCallersOnly]
    private static void OnDragEnded(IntPtr self, IntPtr sel, IntPtr session, CGPoint point, nuint operation) => With(self, w =>
    {
        var roots = w._dragRoots;
        string what = w._dragWhat ?? "";
        w._dragRoots = null;
        w._suppressLeftUp = false;
        w._leftDown = false;
        if (roots == null) return;
        if (operation != 0) w.Toast($"Hotovo – {what} zkopírováno na tento Mac.");
        else w.SaveToDownloads(roots, what); // puštěno mimo cíl → uložit do Stažených
    });

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern IntPtr CGEventCreate(IntPtr source);
    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern CGPoint CGEventGetLocation(IntPtr ev);

    private static bool LeftPressed() => (ObjC.SendRetUInt(ObjC.Cls("NSEvent"), ObjC.Sel("pressedMouseButtons")) & 1) != 0;

    /// <summary>Poloha kurzoru v globálních souřadnicích (body, počátek vlevo nahoře – jako CGWindowList).</summary>
    private static CGPoint GlobalMouse()
    {
        IntPtr ev = CGEventCreate(IntPtr.Zero);
        if (ev == IntPtr.Zero) return default;
        try { return CGEventGetLocation(ev); }
        finally { Native.CFRelease(ev); }
    }

    /// <summary>Složka tohoto Macu pod bodem: okno Finderu nebo Plocha. Nad jinou aplikací null (→ Stažené soubory).</summary>
    private async Task<string?> LocalFolderAtAsync(CGPoint p)
    {
        try
        {
            var hit = WindowLookup.At(p.X, p.Y);
            if (hit == null) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
            if (hit.Owner != "Finder") return null; // jiná aplikace (i okno RemoteScreen) → Stažené soubory
            var target = await WindowLookup.ResolveAsync(p.X, p.Y);
            return target.OpenWithApp == null ? target.Path : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Místo puštění souborů: " + ex.Message);
            return null;
        }
    }

    private void SaveToDownloads(string[] roots, string what) => SaveTo(roots, what, null);

    /// <summary>Přesune stažené položky do složky (bez přepisování); null = Stažené soubory.</summary>
    private void SaveTo(string[] roots, string what, string? folder)
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = folder ?? Path.Combine(home, "Downloads");
            bool toDownloads = folder == null;
            Directory.CreateDirectory(downloads);
            string? first = null;
            foreach (var src in roots)
            {
                string name = Path.GetFileName(src);
                string dest = Path.Combine(downloads, name);
                for (int i = 2; File.Exists(dest) || Directory.Exists(dest); i++)
                    dest = Path.Combine(downloads, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
                if (Directory.Exists(src)) Directory.Move(src, dest); else File.Move(src, dest);
                first ??= dest;
            }
            string where = toDownloads ? "Stažené soubory"
                : downloads == Path.Combine(home, "Desktop") ? "Plocha"
                : "složka „" + Path.GetFileName(downloads) + "“";
            Toast($"Hotovo – {what} uloženo: {where}");
            if (first != null && toDownloads) // Stažené nejsou vidět – ukázat; okno Finderu/Plochu uživatel vidí
            {
                IntPtr url = ObjC.Send(ObjC.Cls("NSURL"), ObjC.Sel("fileURLWithPath:"), ObjC.Str(first));
                IntPtr arr = ObjC.Send(ObjC.Cls("NSArray"), ObjC.Sel("arrayWithObject:"), url);
                ObjC.Send(ObjC.Get(ObjC.Cls("NSWorkspace"), "sharedWorkspace"), ObjC.Sel("activateFileViewerSelectingURLs:"), arr);
            }
        }
        catch (Exception ex)
        {
            Toast("Uložení selhalo: " + ex.Message, error: true);
        }
    }

    /// <summary>Smaže dočasné soubory ze starších tažení (Finder je mezitím zkopíroval).</summary>
    private static void CleanupDragTemp()
    {
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(DragTempRoot)) return;
                foreach (var dir in Directory.GetDirectories(DragTempRoot))
                    if (Directory.GetCreationTime(dir) < DateTime.Now.AddHours(-1))
                        try { Directory.Delete(dir, true); } catch { }
            }
            catch { }
        });
    }
}
