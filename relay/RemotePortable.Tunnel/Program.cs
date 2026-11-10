using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using RemotePortable.Shared;

if (args.Length < 4 || (args[0] != "host" && args[0] != "join"))
{
    Console.WriteLine("Uso: RemotePortable.Tunnel host|join RELAY_DNS PORTA_RELAY ID_DISPOSITIVO [PORTA_LOCAL] [FINGERPRINT]");
    return;
}
var mode = args[0];
var relayHost = args[1];
var relayPort = int.Parse(args[2]);
var targetId = args[3].ToLowerInvariant();
var localPort = args.Length > 4 ? int.Parse(args[4]) : (mode == "host" ? 47890 : 47891);
var hostFingerprint = mode == "host" && args.Length > 5 ? args[5].Trim().ToUpperInvariant() : new string('0', 64);
if (hostFingerprint.Length != 64 || !hostFingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Fingerprint inválido");
if (targetId.Length != 64 || !targetId.All(Uri.IsHexDigit)) throw new ArgumentException("ID de dispositivo inválido");
var identity = DeviceIdentity.LoadOrCreate();
if (mode == "host" && !StringComparer.OrdinalIgnoreCase.Equals(targetId, identity.Id))
    throw new ArgumentException("O host deve publicar o ID derivado da identidade local.");

async Task<SslStream> ConnectRelay(CancellationToken ct)
{
    await RelayClientAuth.EnsureApprovedAsync(relayHost, relayPort, identity, ct);
    var tcp = new TcpClient();
    await tcp.ConnectAsync(relayHost, relayPort, ct);
    var ssl = new SslStream(tcp.GetStream(), false);
    try
    {
        await ssl.AuthenticateAsClientAsync(relayHost, ct);
        await RelayClientAuth.AuthenticateAsync(ssl, identity, mode == "host" ? "HOST" : "JOIN", targetId, ct);
        var answer = await RelayClientAuth.ReadLineAsync(ssl, ct);
        if (answer.StartsWith("ERR")) throw new IOException("Relay: " + answer);
        if (answer != (mode == "host" ? "WAIT" : "READY")) throw new InvalidDataException("Resposta inesperada: " + answer);
        return ssl;
    }
    catch { ssl.Dispose(); tcp.Dispose(); throw; }
}
async Task Tunnel(Stream local, Stream remote, CancellationToken ct)
{
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
    async Task Pump(Stream a, Stream b)
    {
        try { await a.CopyToAsync(b, stop.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { stop.Cancel(); }
    }
    await Task.WhenAll(Pump(local, remote), Pump(remote, local));
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
if (mode == "host")
{
    _ = Task.Run(async () =>
    {
        var name = new string(Environment.MachineName.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.').Take(64).ToArray());
        if (name.Length == 0) name = "RemotePortable";
        var platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other";
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await RelayClientAuth.EnsureApprovedAsync(relayHost, relayPort, identity, shutdown.Token);
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(relayHost, relayPort, shutdown.Token);
                using var tls = new SslStream(tcp.GetStream(), false);
                await tls.AuthenticateAsClientAsync(relayHost, shutdown.Token);
                await RelayClientAuth.AuthenticateAsync(tls, identity, "REGISTER", identity.Id, shutdown.Token);
                await RelayClientAuth.WriteLineAsync(tls, $"{name} {platform} {hostFingerprint}", shutdown.Token);
                if (await RelayClientAuth.ReadLineAsync(tls, shutdown.Token) != "OK") throw new IOException("Registro recusado pelo Relay.");
                Console.WriteLine($"Disponível na lista: {name} ({identity.Id})");
                while (!shutdown.IsCancellationRequested)
                {
                    await Task.Delay(15000, shutdown.Token);
                    await RelayClientAuth.WriteLineAsync(tls, "PING", shutdown.Token);
                    if (await RelayClientAuth.ReadLineAsync(tls, shutdown.Token) != "PONG") throw new IOException("Heartbeat perdido.");
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.Error.WriteLine("Descoberta: " + ex.Message); }
            try { await Task.Delay(3000, shutdown.Token); } catch (OperationCanceledException) { break; }
        }
    });
    Console.WriteLine($"HOST: compartilhamento RemotePortable deve estar ativo em 127.0.0.1:{localPort}");
    while (!shutdown.IsCancellationRequested)
    {
        try
        {
            using var relay = await ConnectRelay(shutdown.Token);
            Console.WriteLine("Aguardando controlador aprovado...");
            var answer = await RelayClientAuth.ReadLineAsync(relay, shutdown.Token);
            if (answer != "READY") throw new IOException("Relay encerrou a espera: " + answer);
            using var local = new TcpClient();
            await local.ConnectAsync("127.0.0.1", localPort, shutdown.Token);
            Console.WriteLine("Sessão iniciada");
            await Tunnel(local.GetStream(), relay, shutdown.Token);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        try { await Task.Delay(3000, shutdown.Token); } catch (OperationCanceledException) { break; }
    }
}
else
{
    var listener = new TcpListener(System.Net.IPAddress.Loopback, localPort);
    listener.Start();
    Console.WriteLine($"JOIN: conecte a 127.0.0.1:{localPort} para controlar {targetId}");
    try
    {
        while (!shutdown.IsCancellationRequested)
        {
            using var local = await listener.AcceptTcpClientAsync(shutdown.Token);
            try
            {
                using var relay = await ConnectRelay(shutdown.Token);
                Console.WriteLine("Conectado ao host remoto");
                await Tunnel(local.GetStream(), relay, shutdown.Token);
            }
            catch (Exception ex) when (!shutdown.IsCancellationRequested) { Console.Error.WriteLine(ex.Message); }
        }
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    finally { listener.Stop(); }
}
