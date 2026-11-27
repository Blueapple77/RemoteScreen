using System.Diagnostics;

namespace RemoteScreen.Host;

/// <summary>Najde změněné části obrazu (dlaždice 64×64), sloučí je do obdélníků a zvolí kvalitu:
/// při velkém pohybu nižší (plynulost), po zklidnění se změněná místa pošlou znovu v plné kvalitě (doostření).</summary>
internal sealed class DeltaEncoder
{
    public const int Tile = 64;

    private int _cols, _rows, _width, _height;
    private bool[] _dirty = Array.Empty<bool>();
    private byte[] _sentQuality = Array.Empty<byte>();  // kvalita, ve které klient dlaždici má
    private long[] _lastChange = Array.Empty<long>();
    private double _motion;                              // klouzavý průměr podílu změněných dlaždic
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Plán jednoho odeslání: obdélníky a jejich kvalita.</summary>
    public readonly record struct Plan(bool Reset, List<(int X, int Y, int W, int H, int Quality)> Rects, int DirtyTiles)
    {
        public bool IsEmpty => Rects.Count == 0;
    }

    /// <summary>Porovná nový snímek s tím, který má klient. prev = null → celý obraz (začátek, změna velikosti).</summary>
    public Plan Compare(RawFrame cur, RawFrame? prev, int quality)
    {
        long now = _clock.ElapsedMilliseconds;
        bool reset = prev == null || !prev.Matches(cur.Width, cur.Height) || cur.Width != _width || cur.Height != _height;
        if (reset) Resize(cur.Width, cur.Height);

        int dirtyCount = 0;
        for (int ty = 0; ty < _rows; ty++)
        for (int tx = 0; tx < _cols; tx++)
        {
            int i = ty * _cols + tx;
            bool d = reset || TileChanged(cur, prev!, tx, ty);
            _dirty[i] = d;
            if (d)
            {
                dirtyCount++;
                _lastChange[i] = now;
            }
        }

        int total = _cols * _rows;
        double fraction = total > 0 ? (double)dirtyCount / total : 0;
        _motion = reset ? 0 : _motion * 0.6 + fraction * 0.4;

        // při pohybu nižší kvalita (méně dat, plynulejší), jinak plná
        int q = _motion > 0.30 ? Math.Max(35, quality - 30)
              : _motion > 0.10 ? Math.Max(45, quality - 15)
              : quality;
        if (reset) q = quality;

        var rects = new List<(int X, int Y, int W, int H, int Quality)>();
        if (dirtyCount > 0)
        {
            if (reset || fraction > 0.6)
            {
                // většina obrazu – jeden obdélník přes celý obraz je levnější
                rects.Add((0, 0, _width, _height, q));
                for (int i = 0; i < total; i++) _sentQuality[i] = (byte)q;
            }
            else
            {
                foreach (var r in Merge(_dirty)) rects.Add((r.X, r.Y, r.W, r.H, q));
                for (int i = 0; i < total; i++) if (_dirty[i]) _sentQuality[i] = (byte)q;
            }
        }

        // doostření: dlaždice poslané v nižší kvalitě, které se ~350 ms nezměnily
        if (q == quality)
        {
            var refine = new bool[total];
            int refineCount = 0, limit = Math.Max(16, total / 4);
            for (int i = 0; i < total && refineCount < limit; i++)
            {
                if (_dirty[i] || _sentQuality[i] >= quality || now - _lastChange[i] < 350) continue;
                refine[i] = true;
                refineCount++;
            }
            if (refineCount > 0)
            {
                foreach (var r in Merge(refine)) rects.Add((r.X, r.Y, r.W, r.H, quality));
                for (int i = 0; i < total; i++) if (refine[i]) _sentQuality[i] = (byte)quality;
            }
        }

        return new Plan(reset, rects, dirtyCount);
    }

    /// <summary>Po chybě kódování: příště poslat celý obraz.</summary>
    public void Invalidate() => _width = _height = 0;

    private void Resize(int w, int h)
    {
        _width = w;
        _height = h;
        _cols = (w + Tile - 1) / Tile;
        _rows = (h + Tile - 1) / Tile;
        int n = _cols * _rows;
        _dirty = new bool[n];
        _sentQuality = new byte[n];
        _lastChange = new long[n];
        _motion = 0;
    }

    private static bool TileChanged(RawFrame cur, RawFrame prev, int tx, int ty)
    {
        int x0 = tx * Tile, y0 = ty * Tile;
        int w = Math.Min(Tile, cur.Width - x0), h = Math.Min(Tile, cur.Height - y0);
        int bytes = w * 4;
        var a = cur.Pixels.AsSpan();
        var b = prev.Pixels.AsSpan();
        for (int y = 0; y < h; y++)
        {
            int o = (y0 + y) * cur.Stride + x0 * 4;
            if (!a.Slice(o, bytes).SequenceEqual(b.Slice(o, bytes))) return true;
        }
        return false;
    }

    /// <summary>Sloučí označené dlaždice do obdélníků: v řádku souvislé úseky, pod sebou stejně široké úseky spojí.
    /// Při velkém počtu obdélníků vrátí jeden ohraničující.</summary>
    private List<(int X, int Y, int W, int H)> Merge(bool[] marks)
    {
        var done = new List<(int X0, int X1, int Y0, int Y1)>(); // v dlaždicích, X1/Y1 bez
        var open = new Dictionary<(int, int), (int Y0, int Y1)>();
        for (int ty = 0; ty < _rows; ty++)
        {
            var runs = new List<(int X0, int X1)>();
            for (int tx = 0; tx < _cols; tx++)
            {
                if (!marks[ty * _cols + tx]) continue;
                int start = tx;
                while (tx + 1 < _cols && marks[ty * _cols + tx + 1]) tx++;
                runs.Add((start, tx + 1));
            }
            var next = new Dictionary<(int, int), (int Y0, int Y1)>();
            foreach (var run in runs)
            {
                if (open.Remove(run, out var o)) next[run] = (o.Y0, ty + 1); // pokračuje o řádek níž
                else next[run] = (ty, ty + 1);
            }
            foreach (var kv in open) done.Add((kv.Key.Item1, kv.Key.Item2, kv.Value.Y0, kv.Value.Y1));
            open = next;
        }
        foreach (var kv in open) done.Add((kv.Key.Item1, kv.Key.Item2, kv.Value.Y0, kv.Value.Y1));

        if (done.Count > 48)
        {
            // příliš mnoho kousků – jeden ohraničující obdélník
            int x0 = done.Min(d => d.X0), x1 = done.Max(d => d.X1), y0 = done.Min(d => d.Y0), y1 = done.Max(d => d.Y1);
            done = new() { (x0, x1, y0, y1) };
        }

        var result = new List<(int X, int Y, int W, int H)>(done.Count);
        foreach (var d in done)
        {
            int x = d.X0 * Tile, y = d.Y0 * Tile;
            int w = Math.Min(d.X1 * Tile, _width) - x, h = Math.Min(d.Y1 * Tile, _height) - y;
            if (w > 0 && h > 0) result.Add((x, y, w, h));
        }
        return result;
    }
}
