using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;
using RemoteScreen.Shared;

namespace RemoteScreen.Host;

/// <summary>Jedno připojení klienta: TLS → ověření hesla → zpracování zpráv.</summary>
internal sealed class ClientSession
{
    private readonly TcpClient _tcp;
    private readonly X509Certificate2 _cert;
    private readonly ServerConfig _cfg;
    private readonly ServerHost _host;
    private readonly IHostPlatform _platform;
    private readonly string Home;
    private readonly string _remote;
    private readonly CancellationTokenSource _cts = new();
    private readonly IInputSink _input;
    private readonly ConcurrentDictionary<int, Process> _execs = new();
    private readonly Dictionary<int, FileStream> _uploads = new();
    private readonly Dictionary<int, string> _uploadPaths = new();

    private FramedConnection _conn = null!;
    private ScreenStreamer? _streamer;
    private string _cwd;
    private long _dragChangeAtMouseDown = -1;

    // automatické sdílení schránky
    private CancellationTokenSource? _clipWatch;
    private readonly object _clipGate = new();
    private string? _clipLast; // poslední text, který mají obě strany stejný

    /// <summary>Hlídá schránku tohoto počítače a změny posílá klientovi (Auto). Text, který právě přišel
    /// od klienta, se zpátky neposílá.</summary>
    private async Task WatchClipboardAsync(CancellationToken ct)
    {
        try
        {
            long last = _platform.ClipboardChangeCount();
            bool counted = last != -1;
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(counted ? 400 : 1500, ct);
                if (counted)
                {
                    long n = _platform.ClipboardChangeCount();
                    if (n == last) continue;
                    last = n;
                }
                string text = await _platform.GetClipboardAsync();
                if (string.IsNullOrEmpty(text) || text.Length > ClipboardText.MaxAutoLength) continue;
                lock (_clipGate)
                {
                    if (ClipboardText.Same(text, _clipLast)) continue;
                    _clipLast = text;
                }
                await SafeSendJsonAsync(MsgType.ClipboardData, new ClipboardText(text, Auto: true));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("Sdílení schránky: " + ex.Message); }
    }

    public bool Authenticated { get; private set; }
    public string ClientName { get; private set; } = "?";
    public string Address { get; }
    public DateTime Since { get; } = DateTime.Now;
    public bool Streaming => _streamer != null;

    public ClientSession(TcpClient tcp, X509Certificate2 cert, ServerConfig cfg, ServerHost host)
    {
        _tcp = tcp;
        _cert = cert;
        _cfg = cfg;
        _host = host;
        _platform = host.Platform;
        Home = _platform.HomeDir;
        _cwd = Home;
        _remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        Address = (tcp.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.MapToIPv4().ToString() ?? _remote;
        var displays = _platform.GetDisplays();
        _input = _platform.CreateInput(displays.FirstOrDefault(d => d.IsMain) ?? displays[0]);
    }

    public async Task RunAsync()
    {
        Log.Info($"Připojení z {_remote}");
        try
        {
            _tcp.NoDelay = true;
            await using var ssl = new SslStream(_tcp.GetStream(), false);

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _cert,
                    ClientCertificateRequired = false,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token);

                _conn = new FramedConnection(ssl);
                if (!await AuthenticateAsync(timeout.Token)) return;
            }

            Log.Ok($"{_remote}: přihlášen");
            Authenticated = true;
            _host.NotifyChanged();

            while (!_cts.IsCancellationRequested)
            {
                var msg = await _conn.ReceiveAsync(_cts.Token);
                if (msg is null) break;
                try
                {
                    await HandleAsync(msg.Value);
                }
                catch (Exception ex) when (ex is not IOException and not OperationCanceledException)
                {
                    Log.Warn($"{_remote}: chyba při zpracování {msg.Value.Type}: {ex.Message}");
                    await _conn.SendJsonAsync(MsgType.Error, new ErrorInfo($"{msg.Value.Type}: {ex.Message}"), _cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException
                                       or System.Security.Authentication.AuthenticationException)
        {
            Log.Info($"{_remote}: {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"{_remote}: {ex}");
        }
        finally
        {
            Cleanup();
            _host.Unregister(this);
            Log.Info($"{_remote}: odpojen");
        }
    }

    private async Task<bool> AuthenticateAsync(CancellationToken ct)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        await _conn.SendJsonAsync(MsgType.AuthChallenge,
            new AuthChallenge(ProtocolInfo.Version, _cfg.Salt, Convert.ToBase64String(nonce), _cfg.Iterations, _cfg.Name), ct);

        var msg = await _conn.ReceiveAsync(ct);
        if (msg is null || msg.Value.Type != MsgType.AuthResponse) return false;
        var resp = msg.Value.Json<AuthResponse>();

        var expected = AuthCrypto.Proof(Convert.FromBase64String(_cfg.Key), nonce);
        byte[] got;
        try { got = Convert.FromBase64String(resp.Proof); }
        catch (FormatException) { got = Array.Empty<byte>(); }

        if (!CryptographicOperations.FixedTimeEquals(expected, got))
        {
            Log.Warn($"{_remote}: špatné heslo ({resp.ClientName})");
            await Task.Delay(1500, ct); // zpomalení zkoušení hesel
            await _conn.SendJsonAsync(MsgType.AuthResult, new AuthResult(false, "Špatné heslo"), ct);
            return false;
        }

        await _conn.SendJsonAsync(MsgType.AuthResult, new AuthResult(true, "OK",
            _cfg.Name, _platform.OsVersion, Home, _platform.GetDisplays(), ServerHost.MacAddresses(), _platform.Name), ct);
        // rozsvítit displej, pokud spal (po probuzení přes síť zůstává vypnutý)
        _platform.WakeDisplay(true);
        ClientName = string.IsNullOrWhiteSpace(resp.ClientName) ? "?" : resp.ClientName;
        Log.Info($"{_remote}: klient {resp.ClientName}");
        return true;
    }

    private async Task HandleAsync(Message m)
    {
        var ct = _cts.Token;
        switch (m.Type)
        {
            // ---------- obraz ----------
            case MsgType.StreamStart:
            {
                var opts = m.Json<StreamStart>();
                _streamer?.Stop();
                _streamer = new ScreenStreamer(_conn, opts, _platform, d => _input.Display = d, ct);
                _platform.WakeDisplay(true);
                _streamer.Start();
                _host.NotifyChanged();
                Log.Info($"{_remote}: obraz displej {opts.DisplayIndex + 1}, {opts.Fps} fps, kvalita {opts.Quality}, šířka {(opts.MaxWidth == 0 ? "plná" : opts.MaxWidth)}");
                break;
            }
            case MsgType.StreamStop:
                _streamer?.Stop();
                _streamer = null;
                _host.NotifyChanged();
                break;
            case MsgType.FrameAck:
                _streamer?.Ack();
                break;

            // ---------- vstup ----------
            case MsgType.MouseMove:
            {
                var (x, y) = Bin.ReadMouseMove(m.Payload);
                _platform.WakeDisplay(false);
                _input.MouseMove(x, y);
                break;
            }
            case MsgType.MouseButton:
            {
                _platform.WakeDisplay(false);
                var (b, down, clicks, x, y) = Bin.ReadMouseButton(m.Payload);
                if (b == 0 && down)
                {
                    try { _dragChangeAtMouseDown = _platform.DragChangeCount(); } catch { }
                }
                _input.MouseButton(b, down, clicks, x, y);
                break;
            }
            case MsgType.MouseScroll:
            {
                var (dy, dx) = Bin.ReadScroll(m.Payload);
                _input.Scroll(dy, dx);
                break;
            }
            case MsgType.Key:
            {
                _platform.WakeDisplay(false);
                var (code, down) = Bin.ReadKey(m.Payload);
                _input.Key(code, down);
                break;
            }
            case MsgType.Text:
            {
                var text = Bin.Utf8(m.Payload);
                _ = Task.Run(() => _input.TypeText(text));
                break;
            }
            case MsgType.ReleaseAll:
                _input.ReleaseAll();
                break;

            // ---------- příkazy ----------
            case MsgType.Exec:
            {
                var req = m.Json<ExecRequest>();
                _ = Task.Run(() => RunExecAsync(req));
                break;
            }
            case MsgType.ExecKill:
            {
                var req = m.Json<ExecKill>();
                if (_execs.TryGetValue(req.Id, out var p))
                    try { p.Kill(entireProcessTree: true); } catch { }
                break;
            }

            // ---------- soubory ----------
            case MsgType.ListDir:
                await _conn.SendJsonAsync(MsgType.DirListing, ListDirectory(m.Json<PathRequest>().Path), ct);
                break;
            case MsgType.Download:
            {
                var req = m.Json<DownloadRequest>();
                _ = Task.Run(() => SendFileAsync(req));
                break;
            }
            case MsgType.UploadStart:
            {
                var req = m.Json<UploadStart>();
                string path = ExpandPath(req.Path);
                try
                {
                    if (!req.Overwrite) path = UploadTarget(req, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // zapisovat do skrytého dočasného souboru vedle cíle; pod skutečným jménem se objeví
                    // až celý (jinak by si nedokončený soubor mohl vzít náhled, indexace, synchronizace do cloudu…)
                    string temp = PartPath(path);
                    File.WriteAllBytes(temp, Array.Empty<byte>());
                    if (OperatingSystem.IsWindows()) File.SetAttributes(temp, FileAttributes.Hidden);
                    _uploads[req.TransferId] = new FileStream(temp, FileMode.Truncate, FileAccess.Write, FileShare.None, 1 << 16, true);
                    _uploadPaths[req.TransferId] = temp;
                    _uploadFinal[req.TransferId] = (path, req.Overwrite);
                    Log.Info($"{_remote}: nahrávání {path} ({req.Size / 1024} kB)");
                }
                catch (Exception ex)
                {
                    await _conn.SendJsonAsync(MsgType.FileOpResult, new FileOpResult("upload", path, false, FriendlyError(ex), req.TransferId), ct);
                }
                break;
            }
            case MsgType.UploadChunk:
            {
                var (id, data) = Bin.ReadFileChunk(m.Payload);
                if (_uploads.TryGetValue(id, out var fs))
                {
                    try
                    {
                        await fs.WriteAsync(data, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // plný disk apod. – soubor zahodit a ohlásit; další části tohoto souboru se ignorují
                        _uploads.Remove(id);
                        _uploadPaths.Remove(id, out var failed);
                        _uploadFinal.Remove(id, out var target);
                        try { await fs.DisposeAsync(); } catch { }
                        if (failed != null) try { File.Delete(failed); } catch { }
                        await _conn.SendJsonAsync(MsgType.FileOpResult, new FileOpResult("upload", target.Path ?? "", false, FriendlyError(ex), id), ct);
                    }
                }
                break;
            }
            case MsgType.UploadEnd:
            {
                var req = m.Json<UploadEnd>();
                if (_uploads.Remove(req.TransferId, out var fs))
                {
                    await fs.DisposeAsync();
                    _uploadPaths.Remove(req.TransferId, out var temp);
                    _uploadFinal.Remove(req.TransferId, out var target);
                    string final = target.Path ?? "";
                    try
                    {
                        // celý soubor je zapsaný → jedním krokem přejmenovat na skutečné jméno
                        if (!target.Overwrite) final = UniquePath(final); // mezitím mohl vzniknout stejnojmenný
                        File.Move(temp!, final, overwrite: target.Overwrite);
                        if (OperatingSystem.IsWindows())
                            File.SetAttributes(final, File.GetAttributes(final) & ~FileAttributes.Hidden);
                        await _conn.SendJsonAsync(MsgType.FileOpResult, new FileOpResult("upload", final, true, null, req.TransferId), ct);
                    }
                    catch (Exception ex)
                    {
                        if (temp != null) try { File.Delete(temp); } catch { }
                        await _conn.SendJsonAsync(MsgType.FileOpResult, new FileOpResult("upload", final, false, FriendlyError(ex), req.TransferId), ct);
                    }
                }
                break;
            }
            case MsgType.Delete:
            {
                string path = ExpandPath(m.Json<PathRequest>().Path);
                var (ok, message) = await _platform.MoveToTrashAsync(path);
                await _conn.SendJsonAsync(MsgType.FileOpResult, new FileOpResult("delete", path, ok, message), ct);
                break;
            }
            case MsgType.DropTargetGet:
            {
                // bod puštění → globální souřadnice na aktuálním displeji
                double? gx = null, gy = null;
                if (m.Payload.Length > 0)
                {
                    var q = m.Json<DropQuery>();
                    if (q.X >= 0 && q.Y >= 0)
                    {
                        var d = _input.Display;
                        gx = d.X + Math.Clamp(q.X, 0f, 1f) * d.Width;
                        gy = d.Y + Math.Clamp(q.Y, 0f, 1f) * d.Height;
                    }
                }
                // AppleScript může chvíli trvat – neblokovat zpracování myši a klávesnice
                _ = Task.Run(async () => await SafeSendJsonAsync(MsgType.DropTargetInfo, await _platform.ResolveDropTargetAsync(gx, gy)));
                break;
            }
            case MsgType.DragQuery:
            {
                long baseline = _dragChangeAtMouseDown;
                _ = Task.Run(async () =>
                {
                    // tažení začalo až po stisku tlačítka → schránka tažení se od té doby změnila
                    long now = _platform.DragChangeCount();
                    var paths = now >= 0 && now != baseline ? _platform.DragFilePaths() : new List<string>();
                    var info = DescribeDrag(paths);
                    if (info.Items.Length > 0) Log.Info($"{_remote}: tažení na PC – {info.Roots.Length} položek, {info.Items.Length} souborů");
                    await SafeSendJsonAsync(MsgType.DragFiles, info);
                });
                break;
            }
            case MsgType.MakeDir:
            {
                string path = ExpandPath(m.Json<PathRequest>().Path);
                try
                {
                    Directory.CreateDirectory(path);
                    await _conn.SendJsonAsync(MsgType.FileOpResult, new FileOpResult("mkdir", path, true, null), ct);
                }
                catch (Exception ex)
                {
                    await _conn.SendJsonAsync(MsgType.FileOpResult, new FileOpResult("mkdir", path, false, FriendlyError(ex)), ct);
                }
                break;
            }

            // ---------- systém ----------
            case MsgType.Action:
            {
                var req = m.Json<ActionRequest>();
                Log.Info($"{_remote}: akce {req.Name} {req.Arg}");
                _ = Task.Run(async () =>
                {
                    var res = await _platform.RunActionAsync(req, _input);
                    await SafeSendJsonAsync(MsgType.ActionResult, res);
                });
                break;
            }
            case MsgType.ClipboardGet:
            {
                string text = await _platform.GetClipboardAsync();
                lock (_clipGate) _clipLast = text;
                await _conn.SendJsonAsync(MsgType.ClipboardData, new ClipboardText(text), ct);
                break;
            }
            case MsgType.ClipboardSet:
            {
                var c = m.Json<ClipboardText>();
                lock (_clipGate) _clipLast = c.Text; // ať se nevrátí zpátky jako „změna“
                await _platform.SetClipboardAsync(c.Text);
                if (!c.Auto)
                    await _conn.SendJsonAsync(MsgType.ActionResult, new ActionResult("clipboard_set", true, "Schránka nastavena"), ct);
                break;
            }
            case MsgType.ClipboardWatch:
            {
                bool on = m.Payload.Length > 0 && m.Payload[0] != 0;
                _clipWatch?.Cancel();
                _clipWatch = null;
                if (on)
                {
                    _clipWatch = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var token = _clipWatch.Token;
                    _ = Task.Run(() => WatchClipboardAsync(token));
                }
                break;
            }

            case MsgType.Ping:
                await _conn.SendAsync(MsgType.Pong, m.Payload, ct);
                break;
            case MsgType.ThumbGet:
            {
                if (m.Payload.Length < 1) break;
                int index = m.Payload[0];
                var display = _platform.GetDisplays().FirstOrDefault(d => d.Index == index);
                if (display == null) break;
                // náhled pro menu (najetí myší na „Monitor N“) – malý, nízká kvalita, mimo zpracování vstupu
                _ = Task.Run(async () =>
                {
                    try
                    {
                        byte[]? jpeg = _platform.CaptureJpeg(display, 360, 55);
                        if (jpeg == null) return;
                        var payload = new byte[jpeg.Length + 1];
                        payload[0] = (byte)index;
                        jpeg.CopyTo(payload, 1);
                        await _conn.SendAsync(MsgType.Thumb, payload, ct);
                    }
                    catch (Exception ex) { Log.Warn("Náhled obrazovky: " + ex.Message); }
                });
                break;
            }
        }
    }

    // ---------------- příkazy ----------------

    private async Task RunExecAsync(ExecRequest req)
    {
        string cmd = req.Command.Trim();

        // „cd“ musí změnit pracovní adresář relace, ne podprocesu
        if (cmd == "cd" || cmd.StartsWith("cd ", StringComparison.Ordinal))
        {
            string arg = cmd.Length > 2 ? cmd[3..].Trim().Trim('"', '\'') : "~";
            if (arg.Length == 0) arg = "~";
            string target = ExpandPath(arg);
            if (Directory.Exists(target))
            {
                _cwd = Path.GetFullPath(target);
                await SafeSendJsonAsync(MsgType.ExecExit, new ExecExit(req.Id, 0, _cwd));
            }
            else
            {
                await SafeSendJsonAsync(MsgType.ExecOutput, new ExecOutput(req.Id, $"cd: složka neexistuje: {arg}", true));
                await SafeSendJsonAsync(MsgType.ExecExit, new ExecExit(req.Id, 1, _cwd));
            }
            return;
        }

        var psi = _platform.CreateShell(cmd, _cwd);
        psi.UseShellExecute = false;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = _cwd;
        psi.StandardOutputEncoding ??= Encoding.UTF8;
        psi.StandardErrorEncoding ??= Encoding.UTF8;

        // výstup jde přes frontu, aby se zachovalo pořadí řádků
        var queue = Channel.CreateUnbounded<ExecOutput>(new UnboundedChannelOptions { SingleReader = true });
        var pump = Task.Run(async () =>
        {
            await foreach (var o in queue.Reader.ReadAllAsync())
                await SafeSendJsonAsync(MsgType.ExecOutput, o);
        });

        int exitCode;
        try
        {
            using var p = new Process { StartInfo = psi };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) queue.Writer.TryWrite(new ExecOutput(req.Id, e.Data, false)); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) queue.Writer.TryWrite(new ExecOutput(req.Id, e.Data, true)); };
            p.Start();
            _execs[req.Id] = p;
            p.StandardInput.Close();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(_cts.Token);
            p.WaitForExit(); // dočíst zbytek výstupu
            exitCode = p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (_execs.TryGetValue(req.Id, out var p)) try { p.Kill(true); } catch { }
            exitCode = -1;
        }
        catch (Exception ex)
        {
            queue.Writer.TryWrite(new ExecOutput(req.Id, ex.Message, true));
            exitCode = -1;
        }
        finally
        {
            _execs.TryRemove(req.Id, out _);
            queue.Writer.TryComplete();
        }

        await pump;
        await SafeSendJsonAsync(MsgType.ExecExit, new ExecExit(req.Id, exitCode, _cwd));
    }

    // ---------------- soubory ----------------

    private string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return _cwd;
        if (path == "~") return Home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)) path = Path.Combine(Home, path[2..]);
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_cwd, path));
    }

    private DirListing ListDirectory(string rawPath)
    {
        string path = string.IsNullOrWhiteSpace(rawPath) ? Home : ExpandPath(rawPath);
        try
        {
            var dir = new DirectoryInfo(path);
            var entries = new List<DirEntry>();
            foreach (var e in dir.EnumerateFileSystemInfos())
            {
                try
                {
                    bool isDir = e is DirectoryInfo;
                    entries.Add(new DirEntry(e.Name, isDir, isDir ? 0 : ((FileInfo)e).Length, e.LastWriteTime));
                }
                catch { /* nečitelná položka */ }
            }
            var sorted = entries
                .OrderByDescending(e => e.IsDir)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new DirListing(dir.FullName, true, null, sorted);
        }
        catch (Exception ex)
        {
            Log.Warn($"{_remote}: složku {path} nelze přečíst: {ex.Message}");
            return new DirListing(path, false, FriendlyError(ex), Array.Empty<DirEntry>());
        }
    }

    // stahování: nejvýš 3 soubory najednou (tažení velké složky jinak otevře stovky souborů naráz)
    private readonly SemaphoreSlim _downloadSlots = new(3);

    // cílové jméno nahrávaného souboru (zapisuje se do dočasného, viz PartPath)
    private readonly Dictionary<int, (string? Path, bool Overwrite)> _uploadFinal = new();

    /// <summary>Skrytý dočasný soubor ve stejné složce (stejný disk → přejmenování je okamžité a atomické).</summary>
    private static string PartPath(string final) =>
        Path.Combine(Path.GetDirectoryName(final)!, "." + Path.GetFileName(final) + ".remotescreen-part");

    // přejmenované kořeny nahrávaných složek: (dávka, kořen) → skutečná cesta
    private readonly Dictionary<(int, string), string> _uploadRoots = new();

    /// <summary>Kam uložit nahrávaný soubor bez přepisování. Soubor: „jméno 2.ext“. Soubor ve složce: přejmenuje
    /// se jednou celá složka („Foo 2“) pro celou dávku, soubory uvnitř zůstanou pohromadě.</summary>
    private string UploadTarget(UploadStart req, string path)
    {
        if (req.Root == null) return UniquePath(path);
        string root = ExpandPath(req.Root);
        if (!path.StartsWith(root, StringComparison.Ordinal)) return UniquePath(path);
        var key = (req.Batch, root);
        if (!_uploadRoots.TryGetValue(key, out var mapped))
        {
            if (_uploadRoots.Count > 200) _uploadRoots.Clear();
            mapped = UniquePath(root);
            _uploadRoots[key] = mapped;
        }
        return mapped + path[root.Length..];
    }

    private async Task SendFileAsync(DownloadRequest req)
    {
        string path = ExpandPath(req.Path);
        try { await _downloadSlots.WaitAsync(_cts.Token); }
        catch (OperationCanceledException) { return; }
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true);
            await _conn.SendJsonAsync(MsgType.FileStart, new FileStart(req.TransferId, Path.GetFileName(path), fs.Length), _cts.Token);
            var buf = new byte[ProtocolInfo.FileChunkSize];
            int n;
            while ((n = await fs.ReadAsync(buf, _cts.Token)) > 0)
                await _conn.SendAsync(MsgType.FileChunk, Bin.FileChunk(req.TransferId, buf.AsSpan(0, n)), _cts.Token);
            await _conn.SendJsonAsync(MsgType.FileEnd, new FileEnd(req.TransferId, true, null), _cts.Token);
            Log.Info($"{_remote}: staženo {path}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await SafeSendJsonAsync(MsgType.FileEnd, new FileEnd(req.TransferId, false, FriendlyError(ex)));
        }
        finally
        {
            _downloadSlots.Release();
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} {i}{ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>Srozumitelná hláška – hlavně pro složky, ke kterým systém nepovolil přístup.</summary>
    private string FriendlyError(Exception ex) => ex switch
    {
        UnauthorizedAccessException => _platform.AccessDeniedMessage,
        DirectoryNotFoundException => "Složka neexistuje.",
        FileNotFoundException => "Soubor neexistuje.",
        IOException io => io.Message,
        _ => ex.Message,
    };

    /// <summary>Rozbalí tažené položky na seznam souborů s relativními cestami (složky rekurzivně).</summary>
    private static DragFiles DescribeDrag(List<string> paths)
    {
        var items = new List<DragItem>();
        var roots = new List<string>();
        foreach (var raw in paths)
        {
            string p = raw.TrimEnd('/', '\\');
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
                        if (FileTransfers.IsJunk(Path.GetFileName(f))) continue;
                        items.Add(new DragItem(f, name + "/" + Path.GetRelativePath(p, f).Replace('\\', '/'), new FileInfo(f).Length));
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

    // ---------------- pomocné ----------------

    public void Disconnect()
    {
        try { _cts.Cancel(); } catch { }
        try { _tcp.Dispose(); } catch { }
    }

    private async Task SafeSendJsonAsync<T>(MsgType type, T value)
    {
        try { await _conn.SendJsonAsync(type, value, _cts.Token); }
        catch { /* spojení už je zavřené */ }
    }

    private void Cleanup()
    {
        try { _cts.Cancel(); } catch { }
        _streamer?.Stop();
        try { _input.ReleaseAll(); } catch { }
        foreach (var p in _execs.Values) try { p.Kill(true); } catch { }
        foreach (var (id, fs) in _uploads)
        {
            fs.Dispose();
            // nedokončený soubor smazat
            if (_uploadPaths.TryGetValue(id, out var path)) try { File.Delete(path); } catch { }
        }
        _uploads.Clear();
        try { _tcp.Dispose(); } catch { }
    }
}
