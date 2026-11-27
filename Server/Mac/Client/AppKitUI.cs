using System.Runtime.InteropServices;

namespace RemoteScreen.Server;

/// <summary>Okno klienta, které chce dostávat události svého NSWindow.</summary>
internal interface IWindowHandler
{
    void WillClose();
    void DidResize() { }
    void DidBecomeKey() { }
    void DidResignKey() { }
}

/// <summary>Pomůcky pro okna klienta v AppKitu (vše volat na hlavním vlákně).</summary>
internal static class UI
{
    // barvy návrhu C
    // barva zvýraznění – volí se v panelu Obraz (výchozí oranžová #FF8A3D)
    private static double _ar = 1.0, _ag = 0.541, _ab = 0.239;

    public static readonly (string Name, string Hex)[] AccentPresets =
    {
        ("Oranžová", "#FF8A3D"), ("Modrá", "#5B8CFF"), ("Zelená", "#3DD68C"), ("Fialová", "#B48CFF"), ("Růžová", "#FF6FA8"),
    };

    public static string AccentHex { get; private set; } = "#FF8A3D";

    public static void SetAccent(string? hex)
    {
        string h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length != 6 || !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int v)) return;
        _ar = ((v >> 16) & 255) / 255.0;
        _ag = ((v >> 8) & 255) / 255.0;
        _ab = (v & 255) / 255.0;
        AccentHex = "#" + h.ToUpperInvariant();
    }

    public static IntPtr Accent => ObjC.Color(_ar, _ag, _ab);
    public static IntPtr AccentA(double alpha) => ObjC.Color(_ar, _ag, _ab, alpha);
    /// <summary>Světlejší akcent pro text a ikony na tmavém pozadí.</summary>
    public static IntPtr AccentText => ObjC.Color(_ar + (1 - _ar) * 0.32, _ag + (1 - _ag) * 0.32, _ab + (1 - _ab) * 0.32);
    public static IntPtr AccentSoft => AccentA(0.18);
    public static IntPtr Text => ObjC.Color(1, 1, 1, 0.94);
    public static IntPtr TextDim => ObjC.Color(1, 1, 1, 0.55);
    public static IntPtr Danger => ObjC.Color(1.0, 0.36, 0.36);
    public static IntPtr Success => ObjC.Color(0.35, 0.85, 0.55);
    public static IntPtr Clear => ObjC.Get(ObjC.Cls("NSColor"), "clearColor");

    public const double WeightRegular = 0, WeightMedium = 0.23, WeightSemibold = 0.3, WeightBold = 0.4;

    // NSWindowStyleMask
    public const nuint StyleTitled = 1, StyleClosable = 2, StyleMiniaturizable = 4, StyleResizable = 8, StyleFullSizeContent = 1 << 15;

    private static IntPtr _target, _windowDelegate;
    private static readonly Dictionary<nint, Action> Actions = new();
    private static readonly Dictionary<IntPtr, IWindowHandler> Windows = new();
    private static nint _nextTag = 1000;
    private static bool _mainMenuBuilt;

    // ---------------- akce ovládacích prvků ----------------

    private static unsafe void EnsureClasses()
    {
        if (_target != IntPtr.Zero) return;

        IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSObject"), "RSActionTarget", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("act:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnAction, "v@:@");
        ObjC.objc_registerClassPair(cls);
        _target = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));

        IntPtr del = ObjC.objc_allocateClassPair(ObjC.Cls("NSObject"), "RSWindowDelegate", 0);
        ObjC.class_addMethod(del, ObjC.Sel("windowWillClose:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnWillClose, "v@:@");
        ObjC.class_addMethod(del, ObjC.Sel("windowDidResize:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnDidResize, "v@:@");
        ObjC.class_addMethod(del, ObjC.Sel("windowDidBecomeKey:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnDidBecomeKey, "v@:@");
        ObjC.class_addMethod(del, ObjC.Sel("windowDidResignKey:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnDidResignKey, "v@:@");
        ObjC.objc_registerClassPair(del);
        _windowDelegate = ObjC.Send(ObjC.Send(del, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    /// <summary>Napojí akci (target/action + tag) na NSControl / NSMenuItem.</summary>
    public static void OnClick(IntPtr control, Action action)
    {
        EnsureClasses();
        nint tag = _nextTag++;
        Actions[tag] = action;
        ObjC.SetInt(control, "setTag:", tag);
        ObjC.Set(control, "setTarget:", _target);
        ObjC.Send(control, ObjC.Sel("setAction:"), ObjC.Sel("act:"));
    }

    /// <summary>Akce na dvojklik (NSTableView).</summary>
    public static void OnDoubleClick(IntPtr control, Action action)
    {
        EnsureClasses();
        nint tag = _nextTag++;
        Actions[tag] = action;
        ObjC.SetInt(control, "setTag:", tag);
        ObjC.Set(control, "setTarget:", _target);
        ObjC.Send(control, ObjC.Sel("setDoubleAction:"), ObjC.Sel("act:"));
    }

    public static IntPtr Popup(IEnumerable<string> items, int selected, Action changed)
    {
        IntPtr p = ObjC.SendRectBool(ObjC.Send(ObjC.Cls("NSPopUpButton"), ObjC.Sel("alloc")), ObjC.Sel("initWithFrame:pullsDown:"),
            new CGRect(0, 0, 200, 28), false);
        foreach (var it in items) ObjC.Send(p, ObjC.Sel("addItemWithTitle:"), ObjC.Str(it));
        ObjC.SetInt(p, "selectItemAtIndex:", selected);
        OnClick(p, changed);
        return p;
    }

    public static int SelectedIndex(IntPtr popup) => (int)ObjC.SendRetInt(popup, ObjC.Sel("indexOfSelectedItem"));

    public static IntPtr Checkbox(string title, bool on, Action changed)
    {
        IntPtr c = ObjC.Send(ObjC.Cls("NSButton"), ObjC.Sel("checkboxWithTitle:target:action:"), ObjC.Str(title), IntPtr.Zero, IntPtr.Zero);
        ObjC.SetInt(c, "setState:", on ? 1 : 0);
        OnClick(c, changed);
        return c;
    }

    public static bool IsChecked(IntPtr checkbox) => ObjC.SendRetInt(checkbox, ObjC.Sel("state")) != 0;

    public static void ForgetAction(IntPtr control)
    {
        if (control == IntPtr.Zero) return;
        Actions.Remove(ObjC.SendRetInt(control, ObjC.Sel("tag")));
    }

    [UnmanagedCallersOnly]
    private static void OnAction(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            if (Actions.TryGetValue(ObjC.SendRetInt(sender, ObjC.Sel("tag")), out var a)) a();
        }
        catch (Exception ex) { Log.Error("Akce okna: " + ex); }
    }

    // ---------------- okna ----------------

    public static IntPtr CreateWindow(CGRect frame, nuint style, string title, IWindowHandler handler)
    {
        EnsureClasses();
        EnsureMainMenu();
        IntPtr w = ObjC.SendInitWindow(ObjC.Send(ObjC.Cls("NSWindow"), ObjC.Sel("alloc")),
            ObjC.Sel("initWithContentRect:styleMask:backing:defer:"), frame, style, 2 /* buffered */, false);
        ObjC.SetBool(w, "setReleasedWhenClosed:", false);
        ObjC.Set(w, "setTitle:", ObjC.Str(title));
        ObjC.Set(w, "setDelegate:", _windowDelegate);
        IntPtr dark = ObjC.Send(ObjC.Cls("NSAppearance"), ObjC.Sel("appearanceNamed:"), ObjC.Str("NSAppearanceNameDarkAqua"));
        if (dark != IntPtr.Zero) ObjC.Set(w, "setAppearance:", dark);
        Windows[w] = handler;
        return w;
    }

    public static void ShowWindow(IntPtr w)
    {
        SetRegularApp(true);
        ObjC.Send(w, ObjC.Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        Activate();
    }

    public static void Activate()
    {
        IntPtr app = ObjC.Get(ObjC.Cls("NSApplication"), "sharedApplication");
        ObjC.SendBool(app, ObjC.Sel("activateIgnoringOtherApps:"), true);
    }

    /// <summary>Pokud je otevřené okno klienta, je aplikace „normální“ (ikona v Docku, Cmd+Tab, hlavní menu).
    /// Bez oken se vrací jen do horní lišty.</summary>
    private static void SetRegularApp(bool regular)
    {
        IntPtr app = ObjC.Get(ObjC.Cls("NSApplication"), "sharedApplication");
        ObjC.SendInt(app, ObjC.Sel("setActivationPolicy:"), regular ? 0 : 1);
    }

    private static IntPtr HandlerWindow(IntPtr notification) => ObjC.Get(notification, "object");

    [UnmanagedCallersOnly]
    private static void OnWillClose(IntPtr self, IntPtr sel, IntPtr notification)
    {
        try
        {
            IntPtr w = HandlerWindow(notification);
            if (Windows.Remove(w, out var h)) h.WillClose();
            ObjC.Set(w, "setDelegate:", IntPtr.Zero);
            if (Windows.Count == 0) SetRegularApp(false);
        }
        catch (Exception ex) { Log.Error("Zavření okna: " + ex); }
    }

    [UnmanagedCallersOnly]
    private static void OnDidResize(IntPtr self, IntPtr sel, IntPtr notification)
    {
        try { if (Windows.TryGetValue(HandlerWindow(notification), out var h)) h.DidResize(); }
        catch (Exception ex) { Log.Error("Okno: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    private static void OnDidBecomeKey(IntPtr self, IntPtr sel, IntPtr notification)
    {
        try { if (Windows.TryGetValue(HandlerWindow(notification), out var h)) h.DidBecomeKey(); }
        catch (Exception ex) { Log.Error("Okno: " + ex.Message); }
    }

    [UnmanagedCallersOnly]
    private static void OnDidResignKey(IntPtr self, IntPtr sel, IntPtr notification)
    {
        try { if (Windows.TryGetValue(HandlerWindow(notification), out var h)) h.DidResignKey(); }
        catch (Exception ex) { Log.Error("Okno: " + ex.Message); }
    }

    // ---------------- hlavní menu (kvůli Cmd+C/V v textových polích) ----------------

    private static void EnsureMainMenu()
    {
        if (_mainMenuBuilt) return;
        _mainMenuBuilt = true;
        IntPtr app = ObjC.Get(ObjC.Cls("NSApplication"), "sharedApplication");
        IntPtr main = ObjC.New("NSMenu");

        IntPtr appMenu = ObjC.New("NSMenu");
        AddMenu(appMenu, "Připojit k počítači…", "k", null, () => ConnectWindow.Show());
        ObjC.Send(appMenu, ObjC.Sel("addItem:"), ObjC.Get(ObjC.Cls("NSMenuItem"), "separatorItem"));
        AddMenu(appMenu, "Skrýt RemoteScreen", "h", "hide:", null);
        AddMenu(appMenu, "Zavřít okno", "w", "performClose:", null);
        ObjC.Send(appMenu, ObjC.Sel("addItem:"), ObjC.Get(ObjC.Cls("NSMenuItem"), "separatorItem"));
        AddMenu(appMenu, "Ukončit RemoteScreen", "q", null, () => MenuBarApp.Quit());
        AddSubmenu(main, "RemoteScreen", appMenu);

        IntPtr edit = ObjC.Send(ObjC.Send(ObjC.Cls("NSMenu"), ObjC.Sel("alloc")), ObjC.Sel("initWithTitle:"), ObjC.Str("Úpravy"));
        AddMenu(edit, "Vyjmout", "x", "cut:", null);
        AddMenu(edit, "Kopírovat", "c", "copy:", null);
        AddMenu(edit, "Vložit", "v", "paste:", null);
        AddMenu(edit, "Vybrat vše", "a", "selectAll:", null);
        AddSubmenu(main, "Úpravy", edit);

        ObjC.Set(app, "setMainMenu:", main);
    }

    private static void AddMenu(IntPtr menu, string title, string key, string? selector, Action? action)
    {
        IntPtr item = ObjC.Send(ObjC.Send(ObjC.Cls("NSMenuItem"), ObjC.Sel("alloc")), ObjC.Sel("initWithTitle:action:keyEquivalent:"),
            ObjC.Str(title), selector != null ? ObjC.Sel(selector) : IntPtr.Zero, ObjC.Str(key));
        if (action != null) OnClick(item, action);
        ObjC.Send(menu, ObjC.Sel("addItem:"), item);
    }

    private static void AddSubmenu(IntPtr main, string title, IntPtr submenu)
    {
        IntPtr item = ObjC.Send(ObjC.Send(ObjC.Cls("NSMenuItem"), ObjC.Sel("alloc")), ObjC.Sel("initWithTitle:action:keyEquivalent:"),
            ObjC.Str(title), IntPtr.Zero, ObjC.Str(""));
        ObjC.Set(item, "setSubmenu:", submenu);
        ObjC.Send(main, ObjC.Sel("addItem:"), item);
    }

    // ---------------- ovládací prvky ----------------

    public static void Add(IntPtr parent, IntPtr child) => ObjC.Send(parent, ObjC.Sel("addSubview:"), child);

    public static void Frame(IntPtr view, double x, double y, double w, double h) =>
        ObjC.SendRect(view, ObjC.Sel("setFrame:"), new CGRect(x, y, w, h));

    /// <summary>NSViewWidthSizable=2, MinXMargin=1, MaxXMargin=4, HeightSizable=16, MinYMargin=8, MaxYMargin=32</summary>
    public static void Autoresize(IntPtr view, nuint mask) => ObjC.SendUInt(view, ObjC.Sel("setAutoresizingMask:"), mask);

    public static IntPtr Label(string text, double size, double weight, IntPtr color, bool wrap = false)
    {
        IntPtr l = wrap
            ? ObjC.Send(ObjC.Cls("NSTextField"), ObjC.Sel("wrappingLabelWithString:"), ObjC.Str(text))
            : ObjC.Send(ObjC.Cls("NSTextField"), ObjC.Sel("labelWithString:"), ObjC.Str(text));
        ObjC.Set(l, "setFont:", ObjC.Font(size, weight));
        ObjC.Set(l, "setTextColor:", color);
        ObjC.SetBool(l, "setSelectable:", false);
        return l;
    }

    public static void SetText(IntPtr label, string text) => ObjC.Set(label, "setStringValue:", ObjC.Str(text));
    public static string GetText(IntPtr field) => ObjC.ToStr(ObjC.Get(field, "stringValue")) ?? "";

    public static IntPtr Button(string title, Action action, bool primary = false)
    {
        IntPtr b = ObjC.NewWithFrame("NSButton", new CGRect(0, 0, 100, 32));
        ObjC.Set(b, "setTitle:", ObjC.Str(title));
        ObjC.SetInt(b, "setBezelStyle:", 1); // NSBezelStyleRounded
        ObjC.SetInt(b, "setControlSize:", 3); // large (macOS 11+); starší systémy ignorují
        if (primary)
        {
            if (ObjC.RespondsTo(b, "setBezelColor:")) ObjC.Set(b, "setBezelColor:", Accent);
            ObjC.Set(b, "setKeyEquivalent:", ObjC.Str("\r"));
        }
        OnClick(b, action);
        return b;
    }

    /// <summary>Plochá ikonová tlačítka v doku (SF Symbols).</summary>
    public static IntPtr IconButton(string symbol, string tooltip, Action action)
    {
        IntPtr b = ObjC.NewWithFrame("NSButton", new CGRect(0, 0, 40, 36));
        ObjC.SetBool(b, "setBordered:", false);
        ObjC.Set(b, "setTitle:", ObjC.Str(""));
        SetSymbol(b, symbol);
        ObjC.Set(b, "setToolTip:", ObjC.Str(tooltip));
        if (ObjC.RespondsTo(b, "setContentTintColor:")) ObjC.Set(b, "setContentTintColor:", Text);
        OnClick(b, action);
        return b;
    }

    public static void SetSymbol(IntPtr button, string symbol)
    {
        IntPtr img = Symbol(symbol, 17);
        if (img != IntPtr.Zero) ObjC.Set(button, "setImage:", img);
        else ObjC.Set(button, "setTitle:", ObjC.Str(symbol));
        ObjC.SetInt(button, "setImagePosition:", 1); // NSImageOnly
    }

    public static IntPtr Symbol(string name, double pointSize)
    {
        IntPtr nsImage = ObjC.Cls("NSImage");
        if (!ObjC.RespondsTo(nsImage, "imageWithSystemSymbolName:accessibilityDescription:")) return IntPtr.Zero;
        IntPtr img = ObjC.Send(nsImage, ObjC.Sel("imageWithSystemSymbolName:accessibilityDescription:"), ObjC.Str(name), IntPtr.Zero);
        if (img == IntPtr.Zero) return IntPtr.Zero;
        IntPtr cfgCls = ObjC.Cls("NSImageSymbolConfiguration");
        if (cfgCls != IntPtr.Zero)
        {
            IntPtr cfg = ObjC.SendDouble2(cfgCls, ObjC.Sel("configurationWithPointSize:weight:"), pointSize, WeightMedium);
            IntPtr sized = ObjC.Send(img, ObjC.Sel("imageWithSymbolConfiguration:"), cfg);
            if (sized != IntPtr.Zero) img = sized;
        }
        return img;
    }

    public static IntPtr TextField(string placeholder, bool secure)
    {
        IntPtr f = ObjC.NewWithFrame(secure ? "NSSecureTextField" : "NSTextField", new CGRect(0, 0, 200, 30));
        ObjC.Set(f, "setPlaceholderString:", ObjC.Str(placeholder));
        ObjC.Set(f, "setFont:", ObjC.Font(15));
        ObjC.SetInt(f, "setBezelStyle:", 1); // rounded
        ObjC.SetBool(f, "setBezeled:", true);
        return f;
    }

    /// <summary>Poloprůhledný panel (NSVisualEffectView).</summary>
    /// <param name="passThrough">Panel nepřijímá myš – kliknutí projdou na pohled pod ním (oznámení přes obraz).</param>
    public static IntPtr Glass(CGRect frame, nint material, bool behindWindow, double cornerRadius = 0, bool passThrough = false)
    {
        IntPtr v = passThrough ? ObjC.NewWithFrame(PassThroughGlassClass(), frame) : ObjC.NewWithFrame("NSVisualEffectView", frame);
        ObjC.SetInt(v, "setMaterial:", material);
        ObjC.SetInt(v, "setBlendingMode:", behindWindow ? 0 : 1);
        ObjC.SetInt(v, "setState:", 1); // vždy aktivní
        if (cornerRadius > 0)
        {
            ObjC.SetBool(v, "setWantsLayer:", true);
            IntPtr layer = ObjC.Get(v, "layer");
            ObjC.SetDouble(layer, "setCornerRadius:", cornerRadius);
            ObjC.SetBool(layer, "setMasksToBounds:", true);
        }
        return v;
    }

    private static IntPtr _passThroughClass;

    /// <summary>NSVisualEffectView, jehož hitTest: vrací nil – myš „propadne“ do pohledu pod ním.</summary>
    private static unsafe IntPtr PassThroughGlassClass()
    {
        if (_passThroughClass != IntPtr.Zero) return _passThroughClass;
        IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSVisualEffectView"), "RSPassThroughGlass", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("hitTest:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, CGPoint, IntPtr>)&NoHit, "@@:{CGPoint=dd}");
        ObjC.objc_registerClassPair(cls);
        return _passThroughClass = cls;
    }

    [UnmanagedCallersOnly]
    private static IntPtr NoHit(IntPtr self, IntPtr sel, CGPoint point) => IntPtr.Zero;

    public static void Rounded(IntPtr view, double radius, IntPtr? background = null, IntPtr? border = null)
    {
        ObjC.SetBool(view, "setWantsLayer:", true);
        IntPtr layer = ObjC.Get(view, "layer");
        ObjC.SetDouble(layer, "setCornerRadius:", radius);
        if (background is IntPtr bg) ObjC.Set(layer, "setBackgroundColor:", ObjC.Get(bg, "CGColor"));
        if (border is IntPtr bc)
        {
            ObjC.Set(layer, "setBorderColor:", ObjC.Get(bc, "CGColor"));
            ObjC.SetDouble(layer, "setBorderWidth:", 1.5);
        }
        else ObjC.SetDouble(layer, "setBorderWidth:", 0);
    }

    // ---------------- dialogy ----------------

    /// <summary>NSAlert; vrací index tlačítka (0 = první).</summary>
    public static int Alert(string title, string text, string[] buttons, IntPtr accessory = default, bool warning = false)
    {
        Activate();
        IntPtr a = ObjC.New("NSAlert");
        ObjC.Set(a, "setMessageText:", ObjC.Str(title));
        ObjC.Set(a, "setInformativeText:", ObjC.Str(text));
        ObjC.SetInt(a, "setAlertStyle:", warning ? 0 : 1); // 0 = warning, 1 = informational
        foreach (var b in buttons) ObjC.Send(a, ObjC.Sel("addButtonWithTitle:"), ObjC.Str(b));
        if (accessory != IntPtr.Zero)
        {
            ObjC.Set(a, "setAccessoryView:", accessory);
            ObjC.Send(ObjC.Get(a, "window"), ObjC.Sel("setInitialFirstResponder:"), accessory);
        }
        nint r = ObjC.SendRetInt(a, ObjC.Sel("runModal"));
        ObjC.Send(a, ObjC.Sel("release"));
        return (int)(r - 1000);
    }

    public static void Info(string title, string text) => Alert(title, text, new[] { "OK" });

    /// <summary>Dotaz na text; null = zrušeno.</summary>
    public static string? Prompt(string title, string text, string initial = "", string placeholder = "")
    {
        IntPtr field = ObjC.NewWithFrame("NSTextField", new CGRect(0, 0, 300, 26));
        ObjC.Set(field, "setStringValue:", ObjC.Str(initial));
        ObjC.Set(field, "setPlaceholderString:", ObjC.Str(placeholder));
        int r = Alert(title, text, new[] { "OK", "Zrušit" }, field);
        return r == 0 ? GetText(field).Trim() : null;
    }

    // ---------------- schránka ----------------

    public static string? PasteboardText()
    {
        IntPtr pb = ObjC.Get(ObjC.Cls("NSPasteboard"), "generalPasteboard");
        return ObjC.ToStr(ObjC.Send(pb, ObjC.Sel("stringForType:"), ObjC.Str("public.utf8-plain-text")));
    }

    public static void SetPasteboardText(string text)
    {
        IntPtr pb = ObjC.Get(ObjC.Cls("NSPasteboard"), "generalPasteboard");
        ObjC.Send(pb, ObjC.Sel("clearContents"));
        ObjC.Send(pb, ObjC.Sel("setString:forType:"), ObjC.Str(text), ObjC.Str("public.utf8-plain-text"));
    }

    /// <summary>Spustí opakovaný časovač na hlavním vlákně.</summary>
    public static IntPtr Timer(double seconds, Action tick)
    {
        EnsureClasses();
        nint tag = _nextTag++;
        Actions[tag] = tick;
        // časovač volá act: s objektem NSTimer – tag nese userInfo (NSNumber)
        IntPtr info = ObjC.SendInt(ObjC.Cls("NSNumber"), ObjC.Sel("numberWithLong:"), tag);
        return ObjC.SendTimer(ObjC.Cls("NSTimer"), ObjC.Sel("scheduledTimerWithTimeInterval:target:selector:userInfo:repeats:"),
            seconds, _timerTarget.Value, ObjC.Sel("tick:"), info, true);
    }

    public static void StopTimer(IntPtr timer)
    {
        if (timer == IntPtr.Zero) return;
        IntPtr info = ObjC.Get(timer, "userInfo");
        if (info != IntPtr.Zero) Actions.Remove(ObjC.SendRetInt(info, ObjC.Sel("longValue")));
        ObjC.Send(timer, ObjC.Sel("invalidate"));
    }

    private static readonly Lazy<IntPtr> _timerTarget = new(CreateTimerTarget);

    private static unsafe IntPtr CreateTimerTarget()
    {
        IntPtr cls = ObjC.objc_allocateClassPair(ObjC.Cls("NSObject"), "RSTimerTarget", 0);
        ObjC.class_addMethod(cls, ObjC.Sel("tick:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnTick, "v@:@");
        ObjC.objc_registerClassPair(cls);
        return ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
    }

    [UnmanagedCallersOnly]
    private static void OnTick(IntPtr self, IntPtr sel, IntPtr timer)
    {
        try
        {
            IntPtr info = ObjC.Get(timer, "userInfo");
            if (info != IntPtr.Zero && Actions.TryGetValue(ObjC.SendRetInt(info, ObjC.Sel("longValue")), out var a)) a();
        }
        catch (Exception ex) { Log.Error("Časovač: " + ex.Message); }
    }

    /// <summary>Jednorázové zpoždění na hlavním vlákně.</summary>
    public static void After(int ms, Action a) =>
        Task.Delay(ms).ContinueWith(_ => MenuBarApp.RunOnMain(a));
}
