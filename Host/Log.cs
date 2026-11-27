namespace RemoteScreen.Host;

/// <summary>Výpis do konzole a (v režimu aplikace) do ~/Library/Logs/RemoteScreen.log.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>Cesta k logu. Mac: ~/Library/Logs/RemoteScreen.log, Windows: nastaví aplikace.</summary>
    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "RemoteScreen.log");

    public static void UseFile()
    {
        if (_file != null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var fi = new FileInfo(FilePath);
            bool truncate = fi.Exists && fi.Length > 5 * 1024 * 1024;
            _file = new StreamWriter(new FileStream(FilePath, truncate ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
        }
        catch { /* bez logu do souboru */ }
    }

    public static void Info(string msg) => Write("INFO", msg, ConsoleColor.Gray);
    public static void Ok(string msg) => Write("OK  ", msg, ConsoleColor.Green);
    public static void Warn(string msg) => Write("WARN", msg, ConsoleColor.Yellow);
    public static void Error(string msg) => Write("ERR ", msg, ConsoleColor.Red);

    private static void Write(string level, string msg, ConsoleColor color)
    {
        lock (Gate)
        {
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
            try
            {
                var old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = old;
            }
            catch { }
            try { _file?.WriteLine($"{level} {line}"); } catch { }
        }
    }
}

public static class ServerOptions
{
    /// <summary>--debug-keys: vypisovat přijaté klávesy (pro ladění klávesnice).</summary>
    public static bool DebugKeys;
}
