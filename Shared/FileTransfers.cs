using System.Collections.Concurrent;

namespace RemoteScreen.Shared;

/// <summary>Nahrávání a stahování souborů přes <see cref="RemoteClient"/> (společné pro klienta na Windows i na Macu).
/// Zprávy FileStart/FileChunk/FileEnd a výsledky nahrávání (FileOpResult) se předávají přes <see cref="Handle"/>
/// z vlákna čtení; zpracují se jen ty, které patří přenosům této třídy.</summary>
public sealed class FileTransfers
{
    private readonly RemoteClient _client;
    private readonly ConcurrentDictionary<int, Download> _downloads = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<FileOpResult>> _uploadResults = new();
    private static int _nextId = 500_000; // společné pro všechny instance – nekoliduje s jinými čísly přenosů
    private static int _nextBatch;
    private int _uploading;
    private long _bytes;

    /// <summary>Bajty souborů přenesené od posledního volání (pro rychlost ve stavové bublině).</summary>
    public long TakeBytes() => Interlocked.Exchange(ref _bytes, 0);

    public FileTransfers(RemoteClient client) => _client = client;

    /// <param name="Files">Kolik souborů se nahrálo.</param>
    /// <param name="RemoteRoots">Skutečné cesty nahraných položek (soubor nebo kořenová složka) na druhém počítači
    /// – po případném přejmenování („report 2.pdf“, „Foo 2“).</param>
    public sealed record UploadResult(int Files, string? Error, List<string> RemoteRoots);

    private sealed class Download
    {
        public required string LocalPath;
        public FileStream? Stream;
        public long Done;
        public readonly TaskCompletionSource<string?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Oddělovač cest podle systému na druhé straně.</summary>
    public bool RemoteIsWindows => _client.Info.Platform == "windows";

    public string Combine(string dir, string relative)
    {
        char sep = RemoteIsWindows ? '\\' : '/';
        string rel = relative.Replace('/', sep).Replace('\\', sep);
        return dir.TrimEnd('/', '\\') + sep + rel.TrimStart(sep);
    }

    /// <summary>Bezpečná místní cesta: relativní cesta od druhého počítače nesmí vést mimo cílovou složku
    /// (např. „../../soubor“). null = neplatná.</summary>
    public static string? SafeLocalPath(string localRoot, string relative)
    {
        try
        {
            string root = Path.GetFullPath(localRoot);
            string full = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/').TrimStart('/')
                .Replace('/', Path.DirectorySeparatorChar)));
            string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(rootWithSep, StringComparison.Ordinal) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Zpracuje zprávu, pokud patří k přenosům této třídy. Volat z vlákna čtení.</summary>
    public bool Handle(Message m)
    {
        switch (m.Type)
        {
            case MsgType.FileStart:
            {
                var s = m.Json<FileStart>();
                if (!_downloads.TryGetValue(s.TransferId, out var d)) return false;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(d.LocalPath)!);
                    d.Stream = new FileStream(d.LocalPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
                }
                catch (Exception ex) { Finish(s.TransferId, ex.Message); }
                return true;
            }
            case MsgType.FileChunk:
            {
                var (id, data) = Bin.ReadFileChunk(m.Payload);
                if (!_downloads.TryGetValue(id, out var d)) return false;
                if (d.Stream != null)
                {
                    try
                    {
                        d.Stream.Write(data);
                        Interlocked.Add(ref d.Done, data.Count);
                        Interlocked.Add(ref _bytes, data.Count);
                    }
                    catch (Exception ex) { Finish(id, ex.Message); } // plný disk apod.
                }
                return true;
            }
            case MsgType.FileEnd:
            {
                var e = m.Json<FileEnd>();
                if (!_downloads.ContainsKey(e.TransferId)) return false;
                Finish(e.TransferId, e.Ok ? null : e.Error ?? "Přenos selhal.");
                return true;
            }
            case MsgType.FileOpResult:
            {
                var r = m.Json<FileOpResult>();
                if (r.Op != "upload" || r.TransferId == 0 || !_uploadResults.TryRemove(r.TransferId, out var tcs)) return false;
                tcs.TrySetResult(r);
                return true;
            }
        }
        return false;
    }

    private void Finish(int id, string? error)
    {
        if (!_downloads.TryRemove(id, out var d)) return;
        try { d.Stream?.Dispose(); } catch { }
        if (error != null) { try { File.Delete(d.LocalPath); } catch { } }
        d.Completion.TrySetResult(error);
    }

    /// <summary>Stáhne položky (např. z tažení ven) do složky. progress(hotovo, celkem). Vrací chybu nebo null.</summary>
    public async Task<string?> DownloadAsync(IReadOnlyList<DragItem> items, string localRoot, Action<long, long>? progress, CancellationToken ct = default)
    {
        long total = items.Sum(i => i.Size);
        var list = new List<Download>();
        foreach (var item in items)
        {
            string? local = SafeLocalPath(localRoot, item.Relative);
            if (local == null) return $"Neplatná cesta od druhého počítače: {item.Relative}";
            int id = Interlocked.Increment(ref _nextId);
            var d = new Download { LocalPath = local };
            _downloads[id] = d;
            list.Add(d);
            _client.PostJson(MsgType.Download, new DownloadRequest(id, item.Path));
        }

        var all = Task.WhenAll(list.Select(d => d.Completion.Task));
        while (!all.IsCompleted)
        {
            await Task.WhenAny(all, Task.Delay(200, ct));
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(list.Sum(d => Interlocked.Read(ref d.Done)), total);
            if (!_client.IsConnected)
            {
                foreach (var kv in _downloads.Where(kv => list.Contains(kv.Value)).ToList()) Finish(kv.Key, "Spojení bylo přerušeno.");
                return "Spojení bylo přerušeno.";
            }
        }
        return (await all).FirstOrDefault(e => e != null);
    }

    /// <summary>Nahraje soubory i složky (rekurzivně) do složky na druhém počítači a počká na potvrzení
    /// každého souboru. Bez přepisování se existující položky nepřepíšou – nové dostanou číslo („Foo 2“).</summary>
    public async Task<UploadResult> UploadAsync(IEnumerable<string> paths, string remoteDir, bool overwrite,
        Action<string, int>? progress, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _uploading, 1) == 1) return new UploadResult(0, "Už probíhá jiné nahrávání.", new());
        var pending = new List<(int Id, Task<FileOpResult> Result, int RootIndex, string RelInRoot)>();
        try
        {
            // (místní soubor, relativní cesta, velikost, index kořene, cesta uvnitř kořene)
            var items = new List<(string Local, string Relative, long Size, int Root, string InRoot)>();
            var roots = new List<(string Name, bool IsDir)>();
            try
            {
                foreach (var raw in paths)
                {
                    string p = raw.TrimEnd('\\', '/');
                    if (File.Exists(p))
                    {
                        roots.Add((Path.GetFileName(p), false));
                        items.Add((p, Path.GetFileName(p), new FileInfo(p).Length, roots.Count - 1, ""));
                    }
                    else if (Directory.Exists(p))
                    {
                        string root = Path.GetFileName(p);
                        roots.Add((root, true));
                        foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                        {
                            var fi = new FileInfo(f);
                            if (IsJunk(fi.Name)) continue;
                            if (OperatingSystem.IsWindows() && (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                            string inRoot = Path.GetRelativePath(p, f).Replace('\\', '/');
                            items.Add((f, root + "/" + inRoot, fi.Length, roots.Count - 1, inRoot));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new UploadResult(0, "Soubory nelze přečíst: " + ex.Message, new());
            }
            if (items.Count == 0) return new UploadResult(0, null, new());

            int batch = Interlocked.Increment(ref _nextBatch);
            long total = items.Sum(i => i.Size), sent = 0;
            int lastPct = -1;
            var lastReport = DateTime.MinValue;
            var buf = new byte[ProtocolInfo.FileChunkSize];
            int index = 0;
            foreach (var (local, rel, _, rootIndex, inRoot) in items)
            {
                index++;
                int id = Interlocked.Increment(ref _nextId);
                var tcs = new TaskCompletionSource<FileOpResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _uploadResults[id] = tcs;
                pending.Add((id, tcs.Task, rootIndex, inRoot));
                string? rootPath = roots[rootIndex].IsDir ? Combine(remoteDir, roots[rootIndex].Name) : null;

                await using var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
                await _client.SendBulkJsonAsync(MsgType.UploadStart,
                    new UploadStart(id, Combine(remoteDir, rel), fs.Length, overwrite, rootPath, batch), ct);
                int n;
                while ((n = await fs.ReadAsync(buf, ct)) > 0)
                {
                    await _client.SendBulkAsync(MsgType.UploadChunk, Bin.FileChunk(id, buf.AsSpan(0, n)), ct);
                    sent += n;
                    Interlocked.Add(ref _bytes, n);
                    int pct = total > 0 ? (int)(sent * 100 / total) : 100;
                    if (pct != lastPct && (DateTime.UtcNow - lastReport).TotalMilliseconds > 120)
                    {
                        lastPct = pct;
                        lastReport = DateTime.UtcNow;
                        progress?.Invoke(ProgressText(sent, total), pct);
                    }
                }
                await _client.SendBulkJsonAsync(MsgType.UploadEnd, new UploadEnd(id), ct);
            }

            // počkat na potvrzení všech souborů (server je zapisuje popořadě)
            progress?.Invoke("dokončuji…", 100);
            var allResults = Task.WhenAll(pending.Select(p => p.Result));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!allResults.IsCompleted)
            {
                await Task.WhenAny(allResults, Task.Delay(250, ct));
                if (!_client.IsConnected) return new UploadResult(0, "Spojení bylo přerušeno.", new());
                if (DateTime.UtcNow > deadline) return new UploadResult(0, "Druhý počítač nepotvrdil uložení souborů.", new());
            }
            var results = await allResults;

            var failed = results.Where(r => !r.Ok).ToList();
            var remoteRoots = new List<string>();
            for (int r = 0; r < roots.Count; r++)
            {
                // skutečná cesta kořene: u souboru přímo, u složky odvozená z prvního nahraného souboru v ní
                var first = pending.Select((p, i) => (p, res: results[i])).FirstOrDefault(x => x.p.RootIndex == r && x.res.Ok);
                if (first.res == null) continue;
                if (!roots[r].IsDir) { remoteRoots.Add(first.res.Path); continue; }
                string actual = first.res.Path.Replace('\\', '/');
                string tail = "/" + first.p.RelInRoot;
                string rootActual = actual.EndsWith(tail, StringComparison.Ordinal) ? actual[..^tail.Length] : Combine(remoteDir, roots[r].Name);
                remoteRoots.Add(RemoteIsWindows ? rootActual.Replace('/', '\\') : rootActual);
            }

            if (failed.Count > 0)
            {
                string msg = failed.Count == results.Length
                    ? $"Nahrání selhalo: {failed[0].Message}"
                    : $"Nahráno {results.Length - failed.Count} z {results.Length} souborů. {Path.GetFileName(failed[0].Path)}: {failed[0].Message}";
                return new UploadResult(results.Length - failed.Count, msg, remoteRoots);
            }
            return new UploadResult(results.Length, null, remoteRoots);
        }
        catch (Exception ex)
        {
            return new UploadResult(0, "Nahrávání selhalo: " + ex.Message, new());
        }
        finally
        {
            foreach (var p in pending) _uploadResults.TryRemove(p.Id, out _);
            Interlocked.Exchange(ref _uploading, 0);
        }
    }

    /// <summary>Systémové pomocné soubory, které se nepřenášejí: .DS_Store, „._*“ (AppleDouble na discích
    /// bez podpory metadat), Icon\r, Thumbs.db, desktop.ini a zámky Office „~$*“.</summary>
    public static bool IsJunk(string name) =>
        name is ".DS_Store" or "Thumbs.db" or "desktop.ini" or "Icon\r" || name.StartsWith("._", StringComparison.Ordinal) || name.StartsWith("~$", StringComparison.Ordinal);

    /// <summary>Co se přenáší, jak to uživatel vidí: název jedné položky, jinak „2 položky“ / „5 položek“.</summary>
    public static string Describe(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1) return Path.GetFileName(paths[0].TrimEnd('\\', '/'));
        return paths.Count switch { >= 2 and <= 4 => $"{paths.Count} položky", _ => $"{paths.Count} položek" };
    }

    /// <summary>Jednotný text průběhu pro oba směry: „45 % · 3,2 MB / 10 MB“.</summary>
    public static string ProgressText(long done, long total) =>
        $"{(total > 0 ? done * 100 / total : 100)} % · {FormatSize(done)} / {FormatSize(total)}";

    public static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} kB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
