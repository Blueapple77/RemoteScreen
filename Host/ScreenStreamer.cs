using System.Diagnostics;
using RemoteScreen.Shared;

namespace RemoteScreen.Host;

/// <summary>Posílá snímky obrazovky klientovi. Na cestě můžou být nejvýš 2 nepotvrzené snímky (FrameAck),
/// takže se rychlost sama přizpůsobí síti, klient se nezahltí a snímání neprostojí čekáním na potvrzení.</summary>
internal sealed class ScreenStreamer
{
    private readonly FramedConnection _conn;
    private readonly StreamStart _opts;
    private readonly Action<DisplayInfo> _onDisplay;
    private readonly IHostPlatform _platform;
    private const int MaxInFlight = 2;
    private readonly SemaphoreSlim _ack = new(0);
    private int _inFlight;
    private readonly CancellationTokenSource _cts;

    public ScreenStreamer(FramedConnection conn, StreamStart opts, IHostPlatform platform, Action<DisplayInfo> onDisplay, CancellationToken parent)
    {
        _platform = platform;
        _conn = conn;
        _opts = opts;
        _onDisplay = onDisplay;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
    }

    public void Start() => _ = Task.Run(LoopAsync);

    public void Stop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Ack()
    {
        if (Interlocked.Decrement(ref _inFlight) < 0) Interlocked.Exchange(ref _inFlight, 0);
        _ack.Release();
    }

    private async Task LoopAsync()
    {
        // klient umí záplaty a platforma surové snímání → posílat jen změněné části
        if (_opts.Delta && await TryDeltaLoopAsync()) return;
        var ct = _cts.Token;
        int fps = Math.Clamp(_opts.Fps, 1, 240);
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        byte[]? last = null;
        var lastSent = Stopwatch.StartNew();
        var displayRefresh = Stopwatch.StartNew();
        DisplayInfo display = PickDisplay();
        int cursorHash = 0;
        var cursorTimer = Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frameTimer = Stopwatch.StartNew();

                if (displayRefresh.Elapsed > TimeSpan.FromSeconds(3))
                {
                    display = PickDisplay();
                    displayRefresh.Restart();
                }

                // tvar kurzoru (šipka, text, ručička…) – nejvýš ~7× za sekundu
                if (cursorTimer.ElapsedMilliseconds > 140)
                {
                    cursorTimer.Restart();
                    CursorShapeData? shape = null;
                    try { shape = _platform.GetCursorIfChanged(ref cursorHash); } catch { }
                    if (shape != null)
                        await _conn.SendAsync(MsgType.CursorShape, Bin.CursorShape(shape.HotX, shape.HotY, shape.Width, shape.Height, shape.Png), ct);
                }

                byte[]? jpeg = await Task.Run(() => _platform.CaptureJpeg(display, _opts.MaxWidth, _opts.Quality), ct);

                // statický obraz neposílat znovu (jen jednou za 2 s jako „keepalive“)
                bool changed = jpeg != null && (last == null || !jpeg.AsSpan().SequenceEqual(last));
                if (jpeg != null && (changed || lastSent.Elapsed > TimeSpan.FromSeconds(2)))
                {
                    // počkat, až klient potvrdí starší snímky (nejvýš MaxInFlight na cestě)
                    while (Volatile.Read(ref _inFlight) >= MaxInFlight)
                    {
                        if (!await _ack.WaitAsync(TimeSpan.FromSeconds(3), ct))
                        {
                            Interlocked.Exchange(ref _inFlight, 0); // potvrzení se ztratilo – začít znovu
                            break;
                        }
                    }
                    Interlocked.Increment(ref _inFlight);
                    await _conn.SendAsync(MsgType.Frame, jpeg, ct);
                    last = jpeg;
                    lastSent.Restart();
                }

                var remaining = interval - frameTimer.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Přenos obrazu skončil: " + ex.Message);
        }
    }

    /// <summary>Režim „jen změny“: surový snímek → porovnání dlaždic → JPEG jen změněných obdélníků.
    /// Statický obraz se vůbec nekóduje ani neposílá. false = platforma surové snímání neumí (běží starý režim).</summary>
    private async Task<bool> TryDeltaLoopAsync()
    {
        var ct = _cts.Token;
        int fps = Math.Clamp(_opts.Fps, 1, 240);
        var interval = TimeSpan.FromSeconds(1.0 / fps);
        var displayRefresh = Stopwatch.StartNew();
        DisplayInfo display = PickDisplay();
        int cursorHash = 0;
        var cursorTimer = Stopwatch.StartNew();
        var delta = new DeltaEncoder();
        RawFrame? cur = null, prev = null; // prev = obraz, který má klient
        int quality = Math.Clamp(_opts.Quality, 10, 100);
        bool started = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frameTimer = Stopwatch.StartNew();

                if (displayRefresh.Elapsed > TimeSpan.FromSeconds(3))
                {
                    display = PickDisplay();
                    displayRefresh.Restart();
                }

                if (cursorTimer.ElapsedMilliseconds > 140)
                {
                    cursorTimer.Restart();
                    CursorShapeData? shape = null;
                    try { shape = _platform.GetCursorIfChanged(ref cursorHash); } catch { }
                    if (shape != null)
                        await _conn.SendAsync(MsgType.CursorShape, Bin.CursorShape(shape.HotX, shape.HotY, shape.Width, shape.Height, shape.Png), ct);
                }

                var target = cur;
                bool ok = await Task.Run(() => _platform.CaptureRaw(display, _opts.MaxWidth, ref target), ct);
                cur = target;
                if (!ok || cur == null)
                {
                    if (!started) return false; // platforma to neumí → starý režim
                    await Task.Delay(200, ct);  // dočasně nejde (zamčená obrazovka…)
                    continue;
                }
                started = true;

                var plan = delta.Compare(cur, prev, quality);
                if (!plan.IsEmpty)
                {
                    // zakódovat obdélníky (víc najednou paralelně)
                    var frame = cur;
                    var encoded = await Task.Run(() =>
                    {
                        var parts = new (int X, int Y, int W, int H, byte[] Jpeg)[plan.Rects.Count];
                        bool failed = false;
                        Parallel.For(0, plan.Rects.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i =>
                        {
                            var r = plan.Rects[i];
                            var jpeg = _platform.EncodeRegion(frame, r.X, r.Y, r.W, r.H, r.Quality);
                            if (jpeg == null) { failed = true; return; }
                            parts[i] = (r.X, r.Y, r.W, r.H, jpeg);
                        });
                        return failed ? null : parts;
                    }, ct);

                    if (encoded == null)
                    {
                        delta.Invalidate(); // příště celý obraz
                        prev = null;
                    }
                    else
                    {
                        while (Volatile.Read(ref _inFlight) >= MaxInFlight)
                        {
                            if (!await _ack.WaitAsync(TimeSpan.FromSeconds(3), ct))
                            {
                                Interlocked.Exchange(ref _inFlight, 0);
                                break;
                            }
                        }
                        Interlocked.Increment(ref _inFlight);
                        await _conn.SendAsync(MsgType.FramePatch, Bin.FramePatch(cur.Width, cur.Height, plan.Reset, encoded), ct);

                        // klient má teď tenhle obraz; příští snímek se nasnímá do starého bufferu
                        (prev, cur) = (cur, prev);
                    }
                }

                var remaining = interval - frameTimer.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("Přenos obrazu skončil: " + ex.Message);
        }
        return true;
    }

    private DisplayInfo PickDisplay()
    {
        var displays = _platform.GetDisplays();
        var d = displays.FirstOrDefault(x => x.Index == _opts.DisplayIndex) ?? displays.FirstOrDefault(x => x.IsMain) ?? displays[0];
        _onDisplay(d);
        return d;
    }
}
