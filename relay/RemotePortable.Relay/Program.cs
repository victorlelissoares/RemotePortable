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
var token = Environment.GetEnvironmentVariable("RELAY_TOKEN") ?? throw new InvalidOperationException("RELAY_TOKEN obrigatório");
if (token.Length < 32) throw new InvalidOperationException("RELAY_TOKEN precisa ter no mínimo 32 caracteres");
var certPath = Environment.GetEnvironmentVariable("RELAY_CERT_PATH") ?? throw new InvalidOperationException("RELAY_CERT_PATH obrigatório");
var certPassword = Environment.GetEnvironmentVariable("RELAY_CERT_PASSWORD") ?? "";
using var cert = new X509Certificate2(certPath, certPassword, X509KeyStorageFlags.EphemeralKeySet);
var waiting = new ConcurrentDictionary<string, HostEntry>();
var discovery = new DiscoveryRegistry();
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($"Relay TLS escutando na porta {port}");

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
                var fields = line.Split(' ', 3);
                if (fields.Length != 3 || !FixedEquals(fields[1], token) ||
                    fields[2].Length is < 6 or > 64 ||
                    !fields[2].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                {
                    await WriteLineAsync(tls, "ERR auth"); return;
                }
                var id = fields[2];
                if (fields[0] == "LIST")
                {
                    // LIST is authorized by the same shared Relay token; entries are opt-in.
                    await WriteLineAsync(tls, JsonSerializer.Serialize(discovery.List().Select(x => new {
                        id = x.Id, name = x.Name, platform = x.Platform, fingerprint = x.Fingerprint
                    })));
                }
                else if (fields[0] == "REGISTER")
                {
                    var owner = Guid.NewGuid().ToString("N");
                    var meta = (await ReadLineAsync(tls, timeout.Token)).Split(' ', 3);
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
                            if (await ReadLineAsync(tls, beatTimeout.Token) != "PING" || !discovery.Heartbeat(id, owner))
                                break;
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
                    // A tarefa HOST é responsável pelo encaminhamento.
                    await entry.Done.Task;
                }
                else await WriteLineAsync(tls, "ERR command");
            }
            catch (Exception ex) { Console.Error.WriteLine($"{DateTimeOffset.UtcNow:O} {ex.GetType().Name}: {ex.Message}"); }
        }
    });
}

static bool FixedEquals(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
{
    using var buffer = new MemoryStream();
    var one = new byte[1];
    while (buffer.Length < 512)
    {
        if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
        if (one[0] == 10) return Encoding.ASCII.GetString(buffer.ToArray()).TrimEnd('\r');
        buffer.WriteByte(one[0]);
    }
    throw new InvalidDataException("Cabeçalho muito longo");
}
static Task WriteLineAsync(Stream stream, string value) =>
    stream.WriteAsync(Encoding.ASCII.GetBytes(value + "\n")).AsTask();

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
