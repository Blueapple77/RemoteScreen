using System.Runtime.InteropServices;

namespace RemoteScreen.Server;

/// <summary>Ikona v horním stavovém řádku macOS (NSStatusItem) s menu: stav, připojení, oprávnění, restart, ukončení.</summary>
internal static class MenuBarApp
{
    private enum Cmd
    {
        Connect = 30,
        Restart = 1, Stop, DisconnectAll, Password, CopyFingerprint, ShowLog, ToggleLogin, Quit,
        OpenScreen = 20, OpenAccessibility, OpenFullDisk, OpenLoginItems,
    }

    private static ServerHost _host = null!;
    private static IntPtr _app, _statusItem, _menu, _target;
    private static string _currentIcon = "";
    private static bool _passwordDialogOpen;
    private static volatile bool _quitting;

    public static int Run(ServerHost host)
    {
        _host = host;
        Log.Info($"Start aplikace: {Environment.ProcessPath}, macOS {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        Log.Info($"DIAG hlavní vlákno: pthread_main_np={ObjC.pthread_main_np()}, NSThread.isMainThread={ObjC.SendRetBool0(ObjC.Cls("NSThread"), ObjC.Sel("isMainThread"))}, managed thread {Environment.CurrentManagedThreadId}");
        Log.Info($"DIAG oprávnění: obrazovka={Permissions.ScreenRecording}, zpřístupnění={Permissions.Accessibility}, plný disk={Permissions.FullDiskAccess}");
        if (!ObjC.LoadAppKit())
        {
            Log.Error("AppKit není k dispozici – ikona v horní liště nepůjde. Server poběží bez ní.");
            _host.Start();
            Thread.Sleep(Timeout.Infinite);
            return 1;
        }
        Log.Info("AppKit načten");

        IntPtr pool = ObjC.objc_autoreleasePoolPush();

        _app = ObjC.Send(ObjC.Cls("NSApplication"), ObjC.Sel("sharedApplication"));
        if (_app == IntPtr.Zero)
        {
            Log.Error("NSApplication se nepodařilo vytvořit.");
            return 1;
        }
        ObjC.SendInt(_app, ObjC.Sel("setActivationPolicy:"), 1); // Accessory = bez ikony v Docku

        Log.Info("NSApplication OK");
        _target = CreateTarget();
        Log.Info("Cíl akcí menu OK");

        IntPtr bar = ObjC.Send(ObjC.Cls("NSStatusBar"), ObjC.Sel("systemStatusBar"));
        _statusItem = ObjC.SendDouble(bar, ObjC.Sel("statusItemWithLength:"), -1.0); // NSVariableStatusItemLength
        ObjC.Send(_statusItem, ObjC.Sel("retain"));

        _menu = ObjC.New("NSMenu");
        ObjC.SendBool(_menu, ObjC.Sel("setAutoenablesItems:"), false);
        ObjC.Send(_menu, ObjC.Sel("setDelegate:"), _target); // menuNeedsUpdate: → menu se sestaví při každém otevření
        ObjC.Send(_app, ObjC.Sel("setDelegate:"), _target);  // applicationShouldTerminate: – nenechat se ukončit omylem
        ObjC.Send(_statusItem, ObjC.Sel("setMenu:"), _menu);

        UpdateIcon();
        Log.Info("Ikona v horní liště vytvořena");
        try
        {
            IntPtr button = ObjC.Send(_statusItem, ObjC.Sel("button"));
            IntPtr image = button != IntPtr.Zero ? ObjC.Send(button, ObjC.Sel("image")) : IntPtr.Zero;
            IntPtr window = button != IntPtr.Zero ? ObjC.Send(button, ObjC.Sel("window")) : IntPtr.Zero;
            bool visible = ObjC.RespondsTo(_statusItem, "isVisible") && ObjC.SendRetBool0(_statusItem, ObjC.Sel("isVisible"));
            Log.Info($"DIAG ikona: statusItem={_statusItem != IntPtr.Zero}, button={button != IntPtr.Zero}, image={image != IntPtr.Zero}, window={window != IntPtr.Zero}, visible={visible}, ikona={_currentIcon}");
        }
        catch (Exception ex) { Log.Warn("DIAG ikona: " + ex.Message); }
        host.StateChanged += RequestRefresh;

        ObjC.objc_autoreleasePoolPop(pool);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Log.Info(_quitting ? "RemoteScreen ukončen uživatelem." : "Proces RemoteScreen končí (ukončen systémem nebo jiným procesem).");

        // první spuštění / oprávnění / start serveru až po rozběhnutí smyčky událostí
        _ = Task.Run(StartupAsync);

        RunOnMain(() => Log.Info($"DIAG fronta hlavního vlákna funguje (pthread_main_np={ObjC.pthread_main_np()})"));
        Log.Info("Spouštím smyčku událostí");
        ObjC.Send(_app, ObjC.Sel("run"));

        // [NSApp run] by se neměl vrátit; pokud ano, pokračovat ve vlastní smyčce událostí
        if (!_quitting)
        {
            Log.Warn("NSApp.run se neočekávaně vrátil – přecházím na vlastní smyčku událostí.");
            RunEventLoop();
        }
        return 0;
    }

    /// <summary>Náhradní smyčka událostí (nextEvent → sendEvent), pro případ, že [NSApp run] skončí.</summary>
    private static void RunEventLoop()
    {
        ObjC.Send(_app, ObjC.Sel("finishLaunching"));
        IntPtr distantFuture = ObjC.Send(ObjC.Cls("NSDate"), ObjC.Sel("distantFuture"));
        ObjC.Send(distantFuture, ObjC.Sel("retain"));
        IntPtr mode = Native.CFStringCreateWithCString(IntPtr.Zero, "kCFRunLoopDefaultMode", Native.kCFStringEncodingUTF8);
        while (!_quitting)
        {
            IntPtr pool = ObjC.objc_autoreleasePoolPush();
            IntPtr ev = ObjC.SendNextEvent(_app, ObjC.Sel("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                ulong.MaxValue, distantFuture, mode, true);
            if (ev != IntPtr.Zero) ObjC.Send(_app, ObjC.Sel("sendEvent:"), ev);
            else Thread.Sleep(50); // pojistka proti točení naprázdno
            ObjC.objc_autoreleasePoolPop(pool);
        }
    }

    private static async Task StartupAsync()
    {
        await Task.Delay(300);
        var cfg = ServerConfig.Load();

        if (!cfg.HasPassword)
        {
            await Dialogs.InfoAsync("Vítejte v RemoteScreen",
                "RemoteScreen umožní ovládat tento Mac z Windows PC v místní síti.\n\n" +
                "Nejdřív nastavte heslo, kterým se bude klient přihlašovat. " +
                "Pak macOS požádá o oprávnění Nahrávání obrazovky a Zpřístupnění – obě povolte.\n\n" +
                "RemoteScreen najdete jako ikonu v horní liště vpravo.");
            await ChangePasswordAsync(firstRun: true);
            cfg = ServerConfig.Load();
        }

        // spouštění po přihlášení si uživatel zapne sám v menu (výchozí stav: vypnuto)

        RunOnMain(() =>
        {
            Log.Info("DIAG kontrola oprávnění na hlavním vlákně");
            Permissions.RequestMissing();
        });
        _host.Start();

        // požádat o povolení ovládat Finder hned při spuštění (když je uživatel u Macu),
        // ne až při puštění souboru na dálku – jinak se soubory uloží na Plochu
        _ = SystemActions.RunProcessAsync("/usr/bin/osascript",
            new[] { "-e", "tell application \"Finder\" to count Finder windows" }, timeoutMs: 120000);

        await Task.Delay(1500);
        Log.Info($"DIAG po startu: položka při přihlášení={LoginItem.IsEnabled}, čeká na schválení={LoginItem.NeedsApproval}");
    }

    // ---------------- Objective-C třída cíle akcí ----------------

    private static unsafe IntPtr CreateTarget()
    {
        IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSObject"), "MRMenuTarget", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("menuAction:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuAction, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("menuNeedsUpdate:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMenuNeedsUpdate, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("refresh:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnRefresh, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("runQueued:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnRunQueued, "v@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("applicationShouldTerminate:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, nuint>)&OnShouldTerminate, "Q@:@");
        ObjC.class_addMethod(cls, ObjC.Sel("applicationShouldTerminateAfterLastWindowClosed:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&OnTerminateAfterLastWindow, "c@:@");
        ObjC.objc_registerClassPair(cls);
        return ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    internal static void RefreshIcon() => RequestRefresh();

    private static void RequestRefresh() =>
        ObjC.SendSelObjBool(_target, ObjC.Sel("performSelectorOnMainThread:withObject:waitUntilDone:"), ObjC.Sel("refresh:"), IntPtr.Zero, false);

    private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> MainQueue = new();

    /// <summary>Spustí akci na hlavním vlákně a počká na její dokončení (z hlavního vlákna rovnou).</summary>
    internal static T RunOnMainSync<T>(Func<T> f)
    {
        if (ObjC.pthread_main_np() == 1) return f();
        T result = default!;
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        RunOnMain(() =>
        {
            try { result = f(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (error != null) throw error;
        return result;
    }

    internal static void RunOnMainSync(Action a) => RunOnMainSync(() => { a(); return 0; });

    /// <summary>Spustí akci na hlavním vlákně (AppKit jinde volat nelze).</summary>
    internal static void RunOnMain(Action a)
    {
        MainQueue.Enqueue(a);
        ObjC.SendSelObjBool(_target, ObjC.Sel("performSelectorOnMainThread:withObject:waitUntilDone:"), ObjC.Sel("runQueued:"), IntPtr.Zero, false);
    }

    [UnmanagedCallersOnly]
    private static void OnRunQueued(IntPtr self, IntPtr sel, IntPtr arg)
    {
        while (MainQueue.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { Log.Error("Hlavní vlákno: " + ex.Message); }
        }
    }

    /// <summary>NSTerminateNow (1) jen při Ukončit z menu, jinak NSTerminateCancel (0).</summary>
    [UnmanagedCallersOnly]
    private static nuint OnShouldTerminate(IntPtr self, IntPtr sel, IntPtr sender)
    {
        if (_quitting) return 1;
        Log.Warn("macOS požádal o ukončení aplikace – odmítnuto (ukončit lze z menu RemoteScreen).");
        return 0;
    }

    [UnmanagedCallersOnly]
    private static byte OnTerminateAfterLastWindow(IntPtr self, IntPtr sel, IntPtr sender) => 0;

    [UnmanagedCallersOnly]
    private static void OnRefresh(IntPtr self, IntPtr sel, IntPtr arg)
    {
        try { UpdateIcon(); } catch (Exception ex) { Log.Error("Ikona: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    private static void OnMenuNeedsUpdate(IntPtr self, IntPtr sel, IntPtr menu)
    {
        try { BuildMenu(menu); } catch (Exception ex) { Log.Error("Menu: " + ex); }
    }

    [UnmanagedCallersOnly]
    private static void OnMenuAction(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try { Handle((Cmd)(int)ObjC.SendRetInt(sender, ObjC.Sel("tag"))); }
        catch (Exception ex) { Log.Error("Akce menu: " + ex); }
    }

    // ---------------- ikona ----------------

    private static void UpdateIcon()
    {
        var sessions = _host.Sessions;
        bool missingPermission = !Permissions.ScreenRecording || !Permissions.Accessibility;

        string[] candidates;
        string tooltip;
        if (!_host.Running || missingPermission)
        {
            candidates = new[] { "exclamationmark.triangle", "display" };
            tooltip = !_host.Running ? "RemoteScreen – server neběží" : "RemoteScreen – chybí oprávnění";
        }
        else if (sessions.Length > 0)
        {
            candidates = new[] { "desktopcomputer.and.arrow.down", "dot.radiowaves.left.and.right", "display" };
            tooltip = "RemoteScreen – ovládá " + string.Join(", ", sessions.Select(s => s.ClientName));
        }
        else
        {
            candidates = new[] { "display", "desktopcomputer" };
            tooltip = "RemoteScreen – čeká na připojení";
        }

        IntPtr button = ObjC.Send(_statusItem, ObjC.Sel("button"));
        ObjC.Send(button, ObjC.Sel("setToolTip:"), ObjC.Str(tooltip));
        if (candidates[0] == _currentIcon) return;

        IntPtr nsImage = ObjC.Cls("NSImage");
        if (ObjC.RespondsTo(nsImage, "imageWithSystemSymbolName:accessibilityDescription:"))
        {
            foreach (var name in candidates)
            {
                IntPtr img = ObjC.Send(nsImage, ObjC.Sel("imageWithSystemSymbolName:accessibilityDescription:"),
                    ObjC.Str(name), ObjC.Str("RemoteScreen"));
                if (img == IntPtr.Zero) continue;
                ObjC.SendBool(img, ObjC.Sel("setTemplate:"), true);
                ObjC.Send(button, ObjC.Sel("setImage:"), img);
                ObjC.Send(button, ObjC.Sel("setTitle:"), ObjC.Str(""));
                _currentIcon = candidates[0];
                return;
            }
        }
        ObjC.Send(button, ObjC.Sel("setTitle:"), ObjC.Str(sessions.Length > 0 ? "MR●" : "MR"));
        _currentIcon = candidates[0];
    }

    // ---------------- menu ----------------

    private static void BuildMenu(IntPtr menu)
    {
        ObjC.Send(menu, ObjC.Sel("removeAllItems"));
        var h = _host;

        // ovládání jiného počítače
        AddItem(menu, "Připojit k počítači…", Cmd.Connect, key: "k");
        foreach (var w in RemoteWindow.Open)
            AddInfo(menu, $"     🖥  ovládáte: {w.Title}");
        AddSeparator(menu);

        // stav serveru
        if (h.Running)
        {
            AddInfo(menu, $"●  RemoteScreen běží – port {h.Config.Port}");
            foreach (var ip in ServerHost.LocalAddresses()) AddInfo(menu, "     " + ip);
        }
        else
        {
            AddInfo(menu, "○  RemoteScreen je zastavený");
        }
        if (h.LastError != null) AddInfo(menu, "⚠️  " + h.LastError);
        AddSeparator(menu);

        // připojení
        var sessions = h.Sessions;
        if (sessions.Length == 0)
        {
            AddInfo(menu, "Tento Mac nikdo neovládá");
        }
        else
        {
            AddInfo(menu, sessions.Length == 1 ? "Mac ovládá:" : $"Mac ovládají ({sessions.Length}):");
            foreach (var s in sessions)
                AddInfo(menu, $"     🖥  {s.ClientName}  ·  {s.Address}  ·  od {s.Since:HH:mm}{(s.Streaming ? "  ·  sleduje obrazovku" : "")}");
            AddItem(menu, "Odpojit všechny", Cmd.DisconnectAll);
        }
        AddSeparator(menu);

        // oprávnění
        AddInfo(menu, "Oprávnění");
        if (Permissions.ScreenRecording) AddInfo(menu, "     ✅  Nahrávání obrazovky");
        else AddItem(menu, "     ⚠️  Nahrávání obrazovky – povolit…", Cmd.OpenScreen);
        if (Permissions.Accessibility) AddInfo(menu, "     ✅  Zpřístupnění (myš a klávesnice)");
        else AddItem(menu, "     ⚠️  Zpřístupnění (myš a klávesnice) – povolit…", Cmd.OpenAccessibility);
        if (Permissions.FullDiskAccess) AddInfo(menu, "     ✅  Plný přístup k disku");
        else AddItem(menu, "     ○  Plný přístup k disku (pro všechny složky) – povolit…", Cmd.OpenFullDisk);
        AddSeparator(menu);

        // ovládání
        AddItem(menu, h.Running ? "Restartovat server" : "Spustit server", Cmd.Restart, key: "r");
        if (h.Running) AddItem(menu, "Zastavit server", Cmd.Stop);
        AddItem(menu, ServerConfig.Load().HasPassword ? "Změnit heslo…" : "Nastavit heslo…", Cmd.Password);
        if (h.Fingerprint.Length > 0) AddItem(menu, "Kopírovat otisk certifikátu", Cmd.CopyFingerprint);
        AddItem(menu, "Zobrazit log", Cmd.ShowLog);
        AddSeparator(menu);
        AddItem(menu, "Spouštět po přihlášení", Cmd.ToggleLogin, check: LoginItem.IsEnabled);
        if (LoginItem.NeedsApproval) AddItem(menu, "     ⚠️  Schválit v Položkách při přihlášení…", Cmd.OpenLoginItems);
        AddSeparator(menu);
        AddItem(menu, "Ukončit RemoteScreen", Cmd.Quit, key: "q");
    }

    private static void AddInfo(IntPtr menu, string title)
    {
        IntPtr item = ObjC.Send(ObjC.Send(ObjC.Cls("NSMenuItem"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithTitle:action:keyEquivalent:"), ObjC.Str(title), IntPtr.Zero, ObjC.Str(""));
        ObjC.SendBool(item, ObjC.Sel("setEnabled:"), false);
        ObjC.Send(menu, ObjC.Sel("addItem:"), item);
        ObjC.Send(item, ObjC.Sel("release"));
    }

    private static void AddItem(IntPtr menu, string title, Cmd cmd, string key = "", bool check = false)
    {
        IntPtr item = ObjC.Send(ObjC.Send(ObjC.Cls("NSMenuItem"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithTitle:action:keyEquivalent:"), ObjC.Str(title), ObjC.Sel("menuAction:"), ObjC.Str(key));
        ObjC.Send(item, ObjC.Sel("setTarget:"), _target);
        ObjC.SendInt(item, ObjC.Sel("setTag:"), (nint)(int)cmd);
        ObjC.SendBool(item, ObjC.Sel("setEnabled:"), true);
        if (check) ObjC.SendInt(item, ObjC.Sel("setState:"), 1);
        ObjC.Send(menu, ObjC.Sel("addItem:"), item);
        ObjC.Send(item, ObjC.Sel("release"));
    }

    private static void AddSeparator(IntPtr menu) =>
        ObjC.Send(menu, ObjC.Sel("addItem:"), ObjC.Send(ObjC.Cls("NSMenuItem"), ObjC.Sel("separatorItem")));

    // ---------------- akce ----------------

    private static void Handle(Cmd cmd)
    {
        switch (cmd)
        {
            case Cmd.Connect:
                ConnectWindow.Show();
                break;
            case Cmd.Restart:
                Task.Run(() => _host.Restart());
                break;
            case Cmd.Stop:
                Task.Run(() => _host.Stop());
                break;
            case Cmd.DisconnectAll:
                Task.Run(() => _host.DisconnectAll());
                break;
            case Cmd.Password:
                Task.Run(() => ChangePasswordAsync(firstRun: false));
                break;
            case Cmd.CopyFingerprint:
                Task.Run(() => SystemActions.SetClipboardAsync(_host.Fingerprint));
                break;
            case Cmd.ShowLog:
                Task.Run(() => SystemActions.RunProcessAsync("/usr/bin/open", new[] { "-a", "Console", Log.FilePath }));
                break;
            case Cmd.ToggleLogin:
                LoginItem.Set(!LoginItem.IsEnabled);
                break;
            case Cmd.OpenScreen:
                Permissions.OpenScreenRecordingSettings();
                break;
            case Cmd.OpenAccessibility:
                Permissions.OpenAccessibilitySettings();
                break;
            case Cmd.OpenFullDisk:
                Permissions.OpenFullDiskAccessSettings();
                break;
            case Cmd.OpenLoginItems:
                Permissions.Open("x-apple.systempreferences:com.apple.LoginItems-Settings.extension");
                break;
            case Cmd.Quit:
                Quit();
                break;
        }
    }

    /// <summary>Ukončí aplikaci (z menu v liště i z hlavního menu aplikace).</summary>
    internal static void Quit()
    {
        foreach (var w in RemoteWindow.Open.ToArray()) w.Disconnect();
        _quitting = true;
        _host.Stop();
        ObjC.Send(_app, ObjC.Sel("terminate:"), IntPtr.Zero);
    }

    internal static ServerHost Host => _host;

    private static async Task ChangePasswordAsync(bool firstRun)
    {
        if (_passwordDialogOpen) return;
        _passwordDialogOpen = true;
        try
        {
            while (true)
            {
                string? p1 = await Dialogs.AskPasswordAsync(firstRun
                    ? "Zvolte heslo pro připojení z Windows (alespoň 6 znaků):"
                    : "Nové heslo pro RemoteScreen (alespoň 6 znaků):");
                if (p1 == null) return;
                if (p1.Length < 6)
                {
                    await Dialogs.InfoAsync("RemoteScreen", "Heslo je příliš krátké – musí mít alespoň 6 znaků.");
                    continue;
                }
                string? p2 = await Dialogs.AskPasswordAsync("Zadejte heslo ještě jednou pro kontrolu:");
                if (p2 == null) return;
                if (p1 != p2)
                {
                    await Dialogs.InfoAsync("RemoteScreen", "Hesla se neshodují, zkuste to znovu.");
                    continue;
                }

                var cfg = ServerConfig.Load();
                cfg.SetPassword(p1);
                cfg.Save();
                Log.Ok("Heslo bylo změněno.");
                if (!firstRun)
                {
                    _host.Restart(); // nové heslo platí pro další připojení
                    await Dialogs.InfoAsync("RemoteScreen", "Heslo bylo změněno.");
                }
                return;
            }
        }
        finally
        {
            _passwordDialogOpen = false;
        }
    }
}

/// <summary>Jednoduché dialogy přes AppleScript (běží v samostatném procesu, takže neblokují menu).</summary>
internal static class Dialogs
{
    public static async Task<string?> AskPasswordAsync(string prompt)
    {
        string script =
            $"display dialog \"{SystemActions.Esc(prompt)}\" default answer \"\" with hidden answer " +
            "with title \"RemoteScreen\" buttons {\"Zrušit\", \"OK\"} default button \"OK\" cancel button \"Zrušit\" with icon note\n" +
            "text returned of result";
        var (code, output) = await SystemActions.RunProcessAsync("/usr/bin/osascript", new[] { "-e", script },
            timeoutMs: 15 * 60 * 1000, mergeStdErr: false);
        if (code != 0) return null;
        return output.EndsWith('\n') ? output[..^1] : output;
    }

    public static Task InfoAsync(string title, string text) =>
        SystemActions.RunProcessAsync("/usr/bin/osascript", new[]
        {
            "-e",
            $"display dialog \"{SystemActions.Esc(text)}\" with title \"{SystemActions.Esc(title)}\" buttons {{\"OK\"}} default button \"OK\" with icon note",
        }, timeoutMs: 15 * 60 * 1000);
}
