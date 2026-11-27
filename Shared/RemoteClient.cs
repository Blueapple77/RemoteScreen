using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Message = RemoteScreen.Shared.Message;

namespace RemoteScreen.Shared;

/// <summary>Síťová vrstva klienta. Odesílání běží přes dvě fronty: prioritní (vstup, ovládání)
/// a hromadnou (nahrávání souborů), aby velký přenos nezdržoval myš a klávesnici.</summary>
public sealed class RemoteClient : IDisposable
{
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private FramedConnection? _conn;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<(MsgType Type, byte[] Payload)> _priority =
        Channel.CreateUnbounded<(MsgType, byte[])>(new UnboundedChannelOptions { SingleReader = false });
    private readonly Channel<(MsgType Type, byte[] Payload)> _bulk =
        Channel.CreateBounded<(MsgType, byte[])>(new BoundedChannelOptions(16) { SingleReader = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly SemaphoreSlim _pending = new(0);
    private int _disconnected;

    public string Host { get; private set; } = "";
    /// <summary>IPv4 adresa druhého počítače (po připojení).</summary>
    public string RemoteAddress { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public AuthResult Info { get; private set; } = new(false, "");

    /// <summary>Volá se na vlákně čtení (ne na UI vlákně).</summary>
    public event Action<Message>? MessageReceived;
    public event Action<string>? Disconnected;

    /// <param name="trust">dotaz na důvěru certifikátu: (otisk, dříve uložený otisk nebo null) → povolit?</param>
    public async Task ConnectAsync(string host, int port, string password, Func<string, string?, bool> trust,
        Func<string, string?> knownFingerprint, CancellationToken ct)
    {
        Host = host;
        _tcp = new TcpClient { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await _tcp.ConnectAsync(host, port, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Server {host}:{port} neodpovídá.");
            }
        }

        byte[]? rawCert = null;
        _ssl = new SslStream(_tcp.GetStream(), false);
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "RemoteScreen",
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            // ověření děláme sami podle uloženého otisku (self-signed certifikát)
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
            {
                rawCert = cert?.GetRawCertData();
                return rawCert != null;
            },
        }, ct);

        Fingerprint = AuthCrypto.Fingerprint(rawCert!);
        RemoteAddress = (_tcp.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.MapToIPv4().ToString() ?? host;
        string? known = knownFingerprint(host);
        if (!string.Equals(known, Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            // dotaz na uživatele dřív, než pošleme cokoliv odvozeného z hesla
            if (!trust(Fingerprint, known))
                throw new AuthenticationException("Certifikát serveru nebyl přijat.");
        }

        _conn = new FramedConnection(_ssl);

        var msg = await _conn.ReceiveAsync(ct) ?? throw new IOException("Server ukončil spojení.");
        if (msg.Type != MsgType.AuthChallenge) throw new InvalidDataException("Neočekávaná odpověď serveru.");
        var ch = msg.Json<AuthChallenge>();
        if (ch.Version != ProtocolInfo.Version)
            throw new InvalidDataException($"Nekompatibilní verze serveru ({ch.Version}, klient {ProtocolInfo.Version}).");

        var proof = await Task.Run(() =>
        {
            var key = AuthCrypto.DeriveKey(password, Convert.FromBase64String(ch.Salt), ch.Iterations);
            return AuthCrypto.Proof(key, Convert.FromBase64String(ch.Nonce));
        }, ct);
        await _conn.SendJsonAsync(MsgType.AuthResponse, new AuthResponse(Convert.ToBase64String(proof), Environment.MachineName), ct);

        msg = await _conn.ReceiveAsync(ct) ?? throw new AuthenticationException("Server ukončil spojení (špatné heslo?).");
        var result = msg.Json<AuthResult>();
        if (!result.Ok) throw new AuthenticationException(result.Message);
        Info = result;
    }

    /// <summary>Spustí čtení a zápis na pozadí. Volat až po připojení handlerů událostí.</summary>
    public void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        string reason = "Spojení ukončeno serverem.";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var msg = await _conn!.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (msg is null) break;
                MessageReceived?.Invoke(msg.Value);
            }
        }
        catch (OperationCanceledException) { reason = "Odpojeno."; }
        catch (Exception ex) { reason = ex.Message; }
        OnDisconnected(reason);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                // každá zpráva ve frontách = jedno uvolnění semaforu
                await _pending.WaitAsync(ct).ConfigureAwait(false);
                if (_priority.Reader.TryRead(out var p))
                    await _conn!.SendAsync(p.Type, p.Payload, ct).ConfigureAwait(false);
                else if (_bulk.Reader.TryRead(out var b))
                    await _conn!.SendAsync(b.Type, b.Payload, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { OnDisconnected(ex.Message); }
    }

    private void OnDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) == 1) return;
        try { _cts.Cancel(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        Disconnected?.Invoke(reason);
    }

    public bool IsConnected => _disconnected == 0;

    // ---------- odesílání ----------

    public void Post(MsgType type, byte[]? payload = null)
    {
        if (_priority.Writer.TryWrite((type, payload ?? Array.Empty<byte>())))
            _pending.Release();
    }

    public void PostJson<T>(MsgType type, T value) =>
        Post(type, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, ProtocolInfo.Json));

    /// <summary>Pro velké přenosy – čeká, když je fronta plná.</summary>
    public async Task SendBulkAsync(MsgType type, byte[] payload, CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _bulk.Writer.WriteAsync((type, payload), linked.Token).ConfigureAwait(false);
        _pending.Release();
    }

    public Task SendBulkJsonAsync<T>(MsgType type, T value, CancellationToken ct = default) =>
        SendBulkAsync(type, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, ProtocolInfo.Json), ct);

    public void Dispose() => OnDisconnected("Odpojeno.");
}
