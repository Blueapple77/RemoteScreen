using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteScreen.Shared;

public static class ProtocolInfo
{
    public const int Version = 4;
    public const int DefaultPort = 47800;
    public const int DiscoveryPort = 47801;
    public const string DiscoveryRequest = "REMOTESCREEN_DISCOVER_V1";
    public const int MaxPayload = 64 * 1024 * 1024;
    public const int FileChunkSize = 256 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>Typy zpráv. Formát rámce: [1 B typ][4 B délka LE][payload].</summary>
public enum MsgType : byte
{
    // autentizace
    AuthChallenge = 1,
    AuthResponse = 2,
    AuthResult = 3,

    // obraz
    StreamStart = 10,
    StreamStop = 11,
    Frame = 12,
    FrameAck = 13,

    // vstup
    MouseMove = 20,
    MouseButton = 21,
    MouseScroll = 22,
    Key = 23,
    Text = 24,
    ReleaseAll = 25,

    // příkazy
    Exec = 30,
    ExecOutput = 31,
    ExecExit = 32,
    ExecKill = 33,

    // soubory
    ListDir = 40,
    DirListing = 41,
    Download = 42,
    FileStart = 43,
    FileChunk = 44,
    FileEnd = 45,
    UploadStart = 46,
    UploadChunk = 47,
    UploadEnd = 48,
    FileOpResult = 49,
    Delete = 50,
    MakeDir = 51,
    DropTargetGet = 52,
    DropTargetInfo = 53,
    DragQuery = 54,
    DragFiles = 55,
    CursorShape = 56,
    /// <summary>Změněné obdélníky obrazu (JPEG) – klient je složí do svého obrazu. Viz <see cref="Bin.FramePatch"/>.</summary>
    FramePatch = 57,
    /// <summary>Žádost o malý náhled obrazovky: [u8 index displeje]. Starší server neodpoví – klient pak náhled neukáže.</summary>
    ThumbGet = 58,
    /// <summary>Náhled obrazovky: [u8 index][jpeg].</summary>
    Thumb = 59,

    // systém
    Action = 60,
    ActionResult = 61,
    ClipboardGet = 62,
    ClipboardData = 63,
    ClipboardSet = 64,
    /// <summary>Klient → server: [u8 1/0] zapnout/vypnout automatické sdílení schránky
    /// (server pak sám posílá ClipboardData s Auto = true, když se jeho schránka změní).</summary>
    ClipboardWatch = 65,

    Ping = 70,
    Pong = 71,
    Error = 99,
}

public readonly record struct Message(MsgType Type, byte[] Payload)
{
    public T Json<T>() =>
        JsonSerializer.Deserialize<T>(Payload, ProtocolInfo.Json)
        ?? throw new InvalidDataException($"Prázdná zpráva {Type}");
}

/// <summary>Rámcování zpráv nad libovolným streamem (zde SslStream). Zápis je zamčený, čtení má být jen z jednoho vlákna.</summary>
public sealed class FramedConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[5];

    public FramedConnection(Stream stream) => _stream = stream;

    public async Task SendAsync(MsgType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (payload.Length <= 64 * 1024)
            {
                // malé zprávy jedním zápisem = jeden TLS záznam
                var buf = new byte[5 + payload.Length];
                buf[0] = (byte)type;
                BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(1), payload.Length);
                payload.Span.CopyTo(buf.AsSpan(5));
                await _stream.WriteAsync(buf, ct).ConfigureAwait(false);
            }
            else
            {
                var header = new byte[5];
                header[0] = (byte)type;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
                await _stream.WriteAsync(header, ct).ConfigureAwait(false);
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendJsonAsync<T>(MsgType type, T value, CancellationToken ct = default) =>
        SendAsync(type, JsonSerializer.SerializeToUtf8Bytes(value, ProtocolInfo.Json), ct);

    /// <summary>Vrátí null, pokud protistrana spojení korektně ukončila.</summary>
    public async Task<Message?> ReceiveAsync(CancellationToken ct = default)
    {
        int n = await _stream.ReadAtLeastAsync(_header, 5, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (n == 0) return null;
        if (n < 5) throw new EndOfStreamException("Spojení přerušeno uprostřed zprávy.");

        var type = (MsgType)_header[0];
        int len = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(1));
        if (len < 0 || len > ProtocolInfo.MaxPayload)
            throw new InvalidDataException($"Neplatná délka zprávy: {len}");

        var payload = len == 0 ? Array.Empty<byte>() : new byte[len];
        if (len > 0)
            await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new Message(type, payload);
    }

    public void Dispose() => _writeLock.Dispose();
}

/// <summary>Binární kódování často posílaných zpráv (vstup, obraz, bloky souborů).</summary>
/// <summary>Jeden změněný obdélník obrazu (souřadnice v pixelech přenášeného obrazu).</summary>
public readonly record struct PatchRect(int X, int Y, int W, int H, ArraySegment<byte> Jpeg);

public static class Bin
{
    /// <summary>[u16 šířka][u16 výška][u8 příznaky: 1 = celý nový obraz][u16 počet] a pro každý obdélník
    /// [u16 x][u16 y][u16 w][u16 h][i32 délka][JPEG].</summary>
    public static byte[] FramePatch(int width, int height, bool reset, IReadOnlyList<(int X, int Y, int W, int H, byte[] Jpeg)> rects)
    {
        int size = 7 + rects.Sum(r => 12 + r.Jpeg.Length);
        var b = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)height);
        b[4] = (byte)(reset ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(5), (ushort)rects.Count);
        int o = 7;
        foreach (var r in rects)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), (ushort)r.X);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 2), (ushort)r.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 4), (ushort)r.W);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o + 6), (ushort)r.H);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o + 8), r.Jpeg.Length);
            r.Jpeg.CopyTo(b, o + 12);
            o += 12 + r.Jpeg.Length;
        }
        return b;
    }

    public static (int Width, int Height, bool Reset, List<PatchRect> Rects) ReadFramePatch(byte[] b)
    {
        int w = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2));
        bool reset = (b[4] & 1) != 0;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(5));
        var list = new List<PatchRect>(count);
        int o = 7;
        for (int i = 0; i < count && o + 12 <= b.Length; i++)
        {
            int x = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o));
            int y = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 2));
            int rw = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 4));
            int rh = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 6));
            int len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o + 8));
            if (len < 0 || o + 12 + len > b.Length) break;
            list.Add(new PatchRect(x, y, rw, rh, new ArraySegment<byte>(b, o + 12, len)));
            o += 12 + len;
        }
        return (w, h, reset, list);
    }

    public static byte[] MouseMove(float x, float y)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0), x);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), y);
        return b;
    }

    public static (float X, float Y) ReadMouseMove(byte[] b) =>
        (BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(0)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(4)));

    /// <param name="button">0 = levé, 1 = pravé, 2 = prostřední</param>
    public static byte[] MouseButton(byte button, bool down, byte clicks, float x, float y)
    {
        var b = new byte[11];
        b[0] = button;
        b[1] = (byte)(down ? 1 : 0);
        b[2] = clicks;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(3), x);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(7), y);
        return b;
    }

    public static (byte Button, bool Down, byte Clicks, float X, float Y) ReadMouseButton(byte[] b) =>
        (b[0], b[1] != 0, b[2],
         BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(3)),
         BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(7)));

    /// <summary>Delta ve stylu Windows (120 = jeden zub kolečka).</summary>
    public static byte[] Scroll(short dy, short dx)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0), dy);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2), dx);
        return b;
    }

    public static (short Dy, short Dx) ReadScroll(byte[] b) =>
        (BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(0)), BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(2)));

    /// <param name="macKeyCode">macOS virtual key code (kVK_*)</param>
    public static byte[] Key(ushort macKeyCode, bool down)
    {
        var b = new byte[3];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), macKeyCode);
        b[2] = (byte)(down ? 1 : 0);
        return b;
    }

    public static (ushort Code, bool Down) ReadKey(byte[] b) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0)), b[2] != 0);

    public static byte[] FileChunk(int transferId, ReadOnlySpan<byte> data)
    {
        var b = new byte[4 + data.Length];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0), transferId);
        data.CopyTo(b.AsSpan(4));
        return b;
    }

    public static (int TransferId, ArraySegment<byte> Data) ReadFileChunk(byte[] b) =>
        (BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(0)), new ArraySegment<byte>(b, 4, b.Length - 4));

    /// <summary>Tvar kurzoru z Macu: hotspot a velikost v bodech (pt) + PNG obrázek.</summary>
    public static byte[] CursorShape(float hotX, float hotY, float widthPt, float heightPt, byte[] png)
    {
        var b = new byte[16 + png.Length];
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0), hotX);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), hotY);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), widthPt);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12), heightPt);
        png.CopyTo(b, 16);
        return b;
    }

    public static (float HotX, float HotY, float WidthPt, float HeightPt, byte[] Png) ReadCursorShape(byte[] b) =>
        (BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(0)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(4)),
         BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(8)), BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(12)),
         b.AsSpan(16).ToArray());

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}

public static class AuthCrypto
{
    public const int DefaultIterations = 200_000;

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    public static byte[] Proof(byte[] key, byte[] nonce) => HMACSHA256.HashData(key, nonce);

    /// <summary>SHA-256 otisk certifikátu ve tvaru AB:CD:EF…</summary>
    public static string Fingerprint(byte[] rawCertificate)
    {
        var hash = SHA256.HashData(rawCertificate);
        return string.Join(':', hash.Select(x => x.ToString("X2")));
    }
}
