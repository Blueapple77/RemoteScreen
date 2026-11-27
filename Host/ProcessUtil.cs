using System.Diagnostics;
using System.Text;

namespace RemoteScreen.Host;

public static class ProcessUtil
{
    /// <summary>Spustí program, počká na konec (s časovým limitem) a vrátí kód a výstup.</summary>
    public static async Task<(int Code, string Output)> RunAsync(string file, IEnumerable<string> args,
        string? stdin = null, int timeoutMs = 20000, bool mergeStdErr = true, IDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Nelze spustit " + file);
        if (stdin != null)
        {
            var bytes = new UTF8Encoding(false).GetBytes(stdin);
            await p.StandardInput.BaseStream.WriteAsync(bytes);
            p.StandardInput.Close();
        }

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "Vypršel časový limit");
        }
        string output = await outTask;
        string err = await errTask;
        if (mergeStdErr && err.Length > 0) output = output.Length > 0 ? output + "\n" + err : err;
        return (p.ExitCode, output);
    }
}
