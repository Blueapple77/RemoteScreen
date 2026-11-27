using System.Diagnostics;
using System.Text;
using RemoteScreen.Shared;

namespace RemoteScreen.Server;

/// <summary>Systémové akce ovládacího panelu (hlasitost, spánek, aplikace, schránka…).</summary>
internal static class SystemActions
{
    public static async Task<ActionResult> RunAsync(ActionRequest req, InputInjector input)
    {
        string arg = req.Arg ?? "";
        try
        {
            switch (req.Name)
            {
                case "volume_up":
                    return await Osa(req.Name, "set volume output volume ((output volume of (get volume settings)) + 6)\noutput volume of (get volume settings)");
                case "volume_down":
                    return await Osa(req.Name, "set volume output volume ((output volume of (get volume settings)) - 6)\noutput volume of (get volume settings)");
                case "volume_mute":
                    return await Osa(req.Name, "set volume output muted not (output muted of (get volume settings))\noutput muted of (get volume settings)");
                case "volume_set":
                    int v = int.TryParse(arg, out var parsed) ? Math.Clamp(parsed, 0, 100) : 50;
                    return await Osa(req.Name, $"set volume output volume {v}\noutput volume of (get volume settings)");
                case "volume_get":
                    return await Osa(req.Name, "output volume of (get volume settings)");

                case "sleep":
                    return await Run(req.Name, "/usr/bin/pmset", "sleepnow");
                case "display_wake":
                    DisplayWake.Wake();
                    return new ActionResult(req.Name, true, "Displej rozsvícen");
                case "display_sleep":
                    return await Run(req.Name, "/usr/bin/pmset", "displaysleepnow");
                case "lock":
                    input.LockScreen();
                    return new ActionResult(req.Name, true, "Obrazovka zamčena");
                case "force_quit":
                    input.Combo(CGConst.FlagCommand | CGConst.FlagAlternate, 0x35); // Cmd+Option+Esc
                    return new ActionResult(req.Name, true, "Otevřeno okno Vynutit ukončení");
                case "mission_control":
                    return await Run(req.Name, "/usr/bin/open", "-a", "Mission Control");
                case "restart":
                    return await Osa(req.Name, "tell application \"System Events\" to restart");
                case "shutdown":
                    return await Osa(req.Name, "tell application \"System Events\" to shut down");
                case "logout":
                    return await Osa(req.Name, "tell application \"System Events\" to log out");

                case "open_with":
                {
                    // Arg: 1. řádek = cesta k aplikaci, další řádky = soubory
                    var lines = arg.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length < 2) return new ActionResult(req.Name, false, "Chybí aplikace nebo soubory");
                    return await Run(req.Name, "/usr/bin/open", new[] { "-a", lines[0] }.Concat(lines.Skip(1)).ToArray());
                }
                case "open_app":
                    return await Run(req.Name, "/usr/bin/open", "-a", arg);
                case "quit_app":
                    return await Osa(req.Name,
                        $"set a to \"{Esc(arg)}\"\n" +
                        "tell application \"System Events\" to set r to exists process a\n" +
                        "if r then\n  tell application a to quit\n  return \"Ukončeno: \" & a\n" +
                        "else\n  return \"Aplikace neběží: \" & a\nend if");
                case "open":
                    return await Run(req.Name, "/usr/bin/open", arg);
                case "list_apps":
                {
                    var r = await Osa(req.Name,
                        "set AppleScript's text item delimiters to linefeed\n" +
                        "tell application \"System Events\" to set n to name of every process whose background only is false\n" +
                        "n as text");
                    return r;
                }
                case "notify":
                    return await Osa(req.Name, $"display notification \"{Esc(arg)}\" with title \"RemoteScreen\"");
                case "say":
                    return await Run(req.Name, "/usr/bin/say", arg);
                case "media_playpause":
                    return await Media(req.Name, "playpause");
                case "media_next":
                    return await Media(req.Name, "next track");
                case "media_prev":
                    return await Media(req.Name, "previous track");
                default:
                    return new ActionResult(req.Name, false, "Neznámá akce: " + req.Name);
            }
        }
        catch (Exception ex)
        {
            return new ActionResult(req.Name, false, ex.Message);
        }
    }

    /// <summary>Ovládá Spotify, pokud běží, jinak Hudbu (Music). Běžící proces se zjišťuje zvlášť,
    /// aby AppleScript nehledal aplikaci, která není nainstalovaná.</summary>
    private static async Task<ActionResult> Media(string name, string command)
    {
        var (_, procs) = await RunProcessAsync("/usr/bin/osascript",
            new[] { "-e", "set AppleScript's text item delimiters to linefeed\ntell application \"System Events\" to set n to name of every process\nn as text" });
        var names = procs.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? app = names.Contains("Spotify") ? "Spotify" : names.Contains("Music") ? "Music" : null;
        if (app == null) return new ActionResult(name, false, "Neběží Spotify ani Hudba");
        var r = await Osa(name, $"tell application \"{app}\" to {command}");
        return r with { Output = r.Ok ? app : r.Output };
    }

    public static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<ActionResult> Osa(string name, string script)
    {
        var (code, output) = await RunProcessAsync("/usr/bin/osascript", new[] { "-e", script });
        return new ActionResult(name, code == 0, output.Trim());
    }

    private static async Task<ActionResult> Run(string name, string file, params string[] args)
    {
        var (code, output) = await RunProcessAsync(file, args);
        return new ActionResult(name, code == 0, code == 0 && output.Trim().Length == 0 ? "OK" : output.Trim());
    }

    public static async Task<string> GetClipboardAsync()
    {
        var (_, output) = await RunProcessAsync("/usr/bin/pbpaste", Array.Empty<string>(), mergeStdErr: false);
        return output;
    }

    public static async Task SetClipboardAsync(string text)
    {
        await RunProcessAsync("/usr/bin/pbcopy", Array.Empty<string>(), stdin: text);
    }

    private static readonly Dictionary<string, string> Utf8Env = new() { ["LANG"] = "en_US.UTF-8", ["LC_CTYPE"] = "UTF-8" };

    public static Task<(int Code, string Output)> RunProcessAsync(string file, IEnumerable<string> args,
        string? stdin = null, int timeoutMs = 20000, bool mergeStdErr = true) =>
        ProcessUtil.RunAsync(file, args, stdin, timeoutMs, mergeStdErr, Utf8Env);
}
