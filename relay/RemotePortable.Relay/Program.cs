using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

var port = int.Parse(Environment.GetEnvironmentVariable("RELAY_PORT") ?? "47900");
var certPath = Environment.GetEnvironmentVariable("RELAY_CERT_PATH") ?? throw new InvalidOperationException("RELAY_CERT_PATH obrigatório");
var certPassword = Environment.GetEnvironmentVariable("RELAY_CERT_PASSWORD") ?? "";
var dataPath = Environment.GetEnvironmentVariable("RELAY_DATA_PATH") ?? "/data";
var approvedPath = Path.Combine(dataPath, "approved-devices.json");
var pendingPath = Path.Combine(dataPath, "pending-devices.json");
var legacyToken = Environment.GetEnvironmentVariable("RELAY_LEGACY_TOKEN");
Directory.CreateDirectory(dataPath);
if (args.Length == 2 && args[0] is "approve" or "revoke")
{
    var target = args[1].ToLowerInvariant();
    if (!ValidDeviceId(target)) throw new ArgumentException("ID de dispositivo inválido.");
    var approved = await LoadKeysAsync(approvedPath, CancellationToken.None);
    if (args[0] == "approve")
    {
        var pending = await LoadKeysAsync(pendingPath, CancellationToken.None);
        if (!pending.Remove(target, out var key)) throw new InvalidOperationException("Dispositivo não encontrado na fila pendente.");
        approved[target] = key;
        await SaveKeysAsync(approvedPath, approved, CancellationToken.None);
        await SaveKeysAsync(pendingPath, pending, CancellationToken.None);
        Console.WriteLine($"Dispositivo aprovado: {target}");
    }
    else
    {
        if (!approved.Remove(target)) throw new InvalidOperationException("Dispositivo aprovado não encontrado.");
        await SaveKeysAsync(approvedPath, approved, CancellationToken.None);
        Console.WriteLine($"Dispositivo revogado: {target}");
    }
    return;
}
using var cert = new X509Certificate2(certPath, certPassword, X509KeyStorageFlags.EphemeralKeySet);
var waiting = new ConcurrentDictionary<string, HostEntry>(StringComparer.OrdinalIgnoreCase);
var discovery = new DiscoveryRegistry();
var enrollmentWriteLock = new SemaphoreSlim(1, 1);
var listener = new TcpListener(IPAddress.Any, port);
listener.Start(256);
Console.WriteLine($"Relay TLS escutando na porta {port}; cadastro manual habilitado; compatibilidade antiga={(string.IsNullOrEmpty(legacyToken) ? "desativada" : "temporária")}");

while (true)
{
    var tcp = await listener.AcceptTcpClientAsync();
    _ = Task.Run(async () =>
    {
        using (tcp)
        {
            try
            {
                tcp.NoDelay = true;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var tls = new SslStream(tcp.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, timeout.Token);
                var line = await ReadLineAsync(tls, timeout.Token);
                if (line.StartsWith("ENROLL ", StringComparison.Ordinal))
                {
                    await EnrollAsync(tls, line[7..], timeout.Token);
                    return;
                }
                if (!line.StartsWith("AUTH ", StringComparison.Ordinal))
                {
                    if (!string.IsNullOrEmpty(legacyToken)) await HandleLegacyAsync(tls, line, legacyToken, timeout.Token);
                    else await WriteLineAsync(tls, "ERR protocol");
                    return;
                }

                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 4 || fields[0] != "AUTH" || !ValidDeviceId(fields[1]) ||
                    fields[2] is not ("LIST" or "REGISTER" or "HOST" or "JOIN"))
                {
                    await WriteLineAsync(tls, "ERR protocol"); return;
                }
                var deviceId = fields[1];
                var targetId = fields[3];
                if (fields[2] == "LIST" ? targetId != "-" : !ValidDeviceId(targetId))
                {
                    await WriteLineAsync(tls, "ERR target"); return;
                }
                var approved = await LoadKeysAsync(approvedPath, timeout.Token);
                if (!approved.TryGetValue(deviceId, out var publicKey))
                {
                    await WriteLineAsync(tls, "ERR device-not-approved"); return;
                }
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await WriteLineAsync(tls, "CHALLENGE " + nonce);
                var signatureLine = await ReadLineAsync(tls, timeout.Token);
                if (!signatureLine.StartsWith("SIGN ", StringComparison.Ordinal) ||
                    !Verify(publicKey, SignaturePayload(fields[2], deviceId, targetId, nonce), signatureLine[5..]))
                {
                    await WriteLineAsync(tls, "ERR signature"); return;
                }
                if ((fields[2] is "HOST" or "REGISTER") && targetId != deviceId)
                {
                    await WriteLineAsync(tls, "ERR identity-mismatch"); return;
                }
                await WriteLineAsync(tls, "OK");

                if (fields[2] == "LIST")
                {
                    await WriteLineAsync(tls, JsonSerializer.Serialize(discovery.List().Select(x => new {
                        id = x.Id, name = x.Name, platform = x.Platform, fingerprint = x.Fingerprint
                    })));
                }
                else if (fields[2] == "REGISTER")
                {
                    var owner = Guid.NewGuid().ToString("N");
                    var meta = (await ReadLineAsync(tls, timeout.Token)).Split(' ', 3);
                    if (meta.Length != 3 || !discovery.Register(deviceId, meta[0], meta[1], meta[2], owner))
                    {
                        await WriteLineAsync(tls, "ERR register"); return;
                    }
                    await WriteLineAsync(tls, "OK");
                    try
                    {
                        while (true)
                        {
                            using var beatTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                            if (await ReadLineAsync(tls, beatTimeout.Token) != "PING" || !discovery.Heartbeat(deviceId, owner)) break;
                            await WriteLineAsync(tls, "PONG");
                        }
                    }
                    finally { discovery.Remove(deviceId, owner); }
                }
                else if (fields[2] == "HOST")
                {
                    var entry = new HostEntry(tls);
                    if (!waiting.TryAdd(targetId, entry)) { await WriteLineAsync(tls, "ERR id-in-use"); return; }
                    try
                    {
                        await WriteLineAsync(tls, "WAIT");
                        using var waitTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
                        await entry.Paired.Task.WaitAsync(waitTimeout.Token);
                        await RelayPairAsync(entry.Stream, entry.Peer!);
                    }
                    finally { entry.Done.TrySetResult(); if (waiting.TryGetValue(targetId, out var current) && ReferenceEquals(current, entry)) waiting.TryRemove(targetId, out _); }
                }
                else if (fields[2] == "JOIN")
                {
                    if (!waiting.TryRemove(targetId, out var entry)) { await WriteLineAsync(tls, "ERR host-offline"); return; }
                    entry.Peer = tls;
                    await WriteLineAsync(entry.Stream, "READY");
                    await WriteLineAsync(tls, "READY");
                    entry.Paired.TrySetResult();
                    await entry.Done.Task;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {ex.GetType().Name}: {ex.Message}"); }
        }
    });
}

async Task EnrollAsync(Stream stream, string publicKey, CancellationToken ct)
{
    try
    {
        var bytes = Convert.FromBase64String(publicKey);
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
        if (consumed != bytes.Length || key.KeySize != 256) throw new CryptographicException("Chave inválida");
        var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var approved = await LoadKeysAsync(approvedPath, ct);
        if (approved.TryGetValue(id, out var existing) && FixedEquals(existing, publicKey))
        {
            await WriteLineAsync(stream, "APPROVED " + id); return;
        }
        await enrollmentWriteLock.WaitAsync(ct);
        try
        {
            var pending = await LoadKeysAsync(pendingPath, ct);
            pending[id] = publicKey;
            await SaveKeysAsync(pendingPath, pending, ct);
        }
        finally { enrollmentWriteLock.Release(); }
        Console.WriteLine($"Novo dispositivo aguardando aprovação: {id}");
        await WriteLineAsync(stream, "PENDING " + id);
    }
    catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException)
    {
        await WriteLineAsync(stream, "ERR invalid-key");
    }
}

async Task HandleLegacyAsync(SslStream tls, string line, string token, CancellationToken ct)
{
    var fields = line.Split(' ', 3);
    if (fields.Length != 3 || !FixedEquals(fields[1], token) || !ValidLegacyId(fields[2]))
    {
        await WriteLineAsync(tls, "ERR auth"); return;
    }
    var id = fields[2];
    if (fields[0] == "LIST")
    {
        await WriteLineAsync(tls, JsonSerializer.Serialize(discovery.List().Select(x => new {
            id = x.Id, name = x.Name, platform = x.Platform, fingerprint = x.Fingerprint
        })));
    }
    else if (fields[0] == "REGISTER")
    {
        var owner = Guid.NewGuid().ToString("N");
        var meta = (await ReadLineAsync(tls, ct)).Split(' ', 3);
        if (meta.Length != 3 || !discovery.Register(id, meta[0], meta[1], meta[2], owner))
        {
            await WriteLineAsync(tls, "ERR register"); return;
        }
        await WriteLineAsync(tls, "OK");
        try
        {
            while (true)
            {
                using var beatTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                if (await ReadLineAsync(tls, beatTimeout.Token) != "PING" || !discovery.Heartbeat(id, owner)) break;
                await WriteLineAsync(tls, "PONG");
            }
        }
        finally { discovery.Remove(id, owner); }
    }
    else if (fields[0] == "HOST")
    {
        var entry = new HostEntry(tls);
        if (!waiting.TryAdd(id, entry)) { await WriteLineAsync(tls, "ERR id-in-use"); return; }
        try
        {
            await WriteLineAsync(tls, "WAIT");
            using var waitTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await entry.Paired.Task.WaitAsync(waitTimeout.Token);
            await RelayPairAsync(entry.Stream, entry.Peer!);
        }
        finally { entry.Done.TrySetResult(); if (waiting.TryGetValue(id, out var current) && ReferenceEquals(current, entry)) waiting.TryRemove(id, out _); }
    }
    else if (fields[0] == "JOIN")
    {
        if (!waiting.TryRemove(id, out var entry)) { await WriteLineAsync(tls, "ERR host-offline"); return; }
        entry.Peer = tls;
        await WriteLineAsync(entry.Stream, "READY");
        await WriteLineAsync(tls, "READY");
        entry.Paired.TrySetResult();
        await entry.Done.Task;
    }
    else await WriteLineAsync(tls, "ERR command");
}

static async Task<Dictionary<string, string>> LoadKeysAsync(string path, CancellationToken ct)
{
    if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
    await using var file = File.OpenRead(path);
    var keys = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(file, cancellationToken: ct);
    return keys is null ? new(StringComparer.OrdinalIgnoreCase) : new(keys, StringComparer.OrdinalIgnoreCase);
}
static async Task SaveKeysAsync(string path, Dictionary<string, string> keys, CancellationToken ct)
{
    var temp = path + ".tmp";
    await using (var file = File.Create(temp)) await JsonSerializer.SerializeAsync(file, keys, cancellationToken: ct);
    File.Move(temp, path, true);
}
static bool ValidDeviceId(string id) => id.Length == 64 && id.All(Uri.IsHexDigit);
static bool ValidLegacyId(string id) => id.Length is >= 6 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
static string SignaturePayload(string action, string id, string target, string nonce) => $"RemotePortable-Relay-v1\n{action}\n{id}\n{target}\n{nonce}";
static bool Verify(string publicKey, string payload, string signature)
{
    try
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
    }
    catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
}
static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
{
    using var buffer = new MemoryStream();
    var one = new byte[1];
    while (buffer.Length < 512)
    {
        if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
        if (one[0] == 10) return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
        buffer.WriteByte(one[0]);
    }
    throw new InvalidDataException("Cabeçalho muito longo");
}
static Task WriteLineAsync(Stream stream, string value) => stream.WriteAsync(Encoding.UTF8.GetBytes(value + "\n")).AsTask();
static async Task RelayPairAsync(Stream host, Stream controller)
{
    using var done = new CancellationTokenSource();
    async Task Pump(Stream src, Stream dst)
    {
        try { await src.CopyToAsync(dst, done.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { done.Cancel(); }
    }
    await Task.WhenAll(Pump(host, controller), Pump(controller, host));
}
sealed class HostEntry(Stream stream)
{
    public Stream Stream { get; } = stream;
    public Stream? Peer { get; set; }
    public TaskCompletionSource Paired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
