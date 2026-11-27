using RemoteScreen.Client.UI;

namespace RemoteScreen.Client;

internal static class Program
{
    public static Icon? AppIcon { get; private set; }

    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteScreen", "error.log");

    [STAThread]
    private static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportError(e.ExceptionObject as Exception);
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    private static void ReportError(Exception? ex)
    {
        string text = ex?.ToString() ?? "Neznámá chyba";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\r\n\r\n");
        }
        catch { }
        MessageBox.Show($"RemoteScreen narazil na chybu:\n\n{ex?.Message}\n\nPodrobnosti jsou v souboru:\n{LogPath}",
            "RemoteScreen – chyba", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static void Run()
    {
        bool background = Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // jen jedna instance – druhé spuštění jen ukáže okno té první
        using var mutex = new Mutex(true, @"Local\RemoteScreen-SingleInstance", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RemoteScreen-Show");
        if (!first)
        {
            if (!background) showSignal.Set();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Theme.Init();
#pragma warning disable WFO5001
        Application.SetColorMode(Theme.Dark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001

        try
        {
            using var s = typeof(Program).Assembly.GetManifestResourceStream("RemoteScreen.ico");
            if (s != null) AppIcon = new Icon(s);
        }
        catch { }

        var settings = ClientSettings.Load();
        Theme.SetAccent(Theme.ParseColor(settings.AccentColor, Theme.Accent));
        var app = new TrayApp(settings, background);

        // signál od dalšího spuštění → ukázat okno
        var wait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) =>
        {
            try { SynchronizationContextHolder.Post(() => app.ShowWindow()); } catch { }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
        SynchronizationContextHolder.Capture();

        Application.Run(app);
        wait.Unregister(null);
        GC.KeepAlive(mutex);
    }
}

/// <summary>Kontext UI vlákna pro volání z jiných vláken.</summary>
internal static class SynchronizationContextHolder
{
    private static SynchronizationContext? _ctx;

    public static void Capture()
    {
        if (SynchronizationContext.Current == null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ctx = SynchronizationContext.Current;
    }

    public static void Post(Action a) => _ctx?.Post(_ => a(), null);
}
