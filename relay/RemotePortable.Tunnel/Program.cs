using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Discovery commands use the same TLS endpoint and shared token as the Relay.
if (args.Length == 4 && args[0] == "list")
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(args[1], int.Parse(args[2]));
    using var tls = new SslStream(tcp.GetStream(), false);
    await tls.AuthenticateAsClientAsync(args[1]);
    await tls.WriteAsync(Encoding.ASCII.GetBytes($"LIST {args[3]} list-all\\n".Replace("\\\\n", "\\n")));
    var response = await ReadLineAsync(tls, CancellationToken.None);
    if (response.StartsWith("ERR")) throw new IOException(response);
    using var doc = JsonDocument.Parse(response);
    foreach (var device in doc.RootElement.EnumerateArray())
        Console.WriteLine($"{device.GetProperty("id").GetString()} | {device.GetProperty("name").GetString()} | {device.GetProperty("platform").GetString()} | {device.GetProperty("fingerprint").GetString()}");
    return;
}
if (args.Length == 7 && args[0] == "register")
{
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(args[1], int.Parse(args[2]));
    using var tls = new SslStream(tcp.GetStream(), false);
    await tls.AuthenticateAsClientAsync(args[1]);
    await tls.WriteAsync(Encoding.ASCII.GetBytes($"REGISTER {args[4]} {args[3]}\\n{args[5]} {System.Runtime.InteropServices.RuntimeInformation.OSDescription.Split(' ')[0].Replace('-', '_')} {args[6]}\\n".Replace("\\\\n", "\\n")));
    if (await ReadLineAsync(tls, CancellationToken.None) != "OK") throw new IOException("Registro recusado pelo Relay.");
    Console.WriteLine("Dispositivo anunciado. Ctrl+C para sair.");
    while (true)
    {
        await Task.Delay(15000);
        await tls.WriteAsync(Encoding.ASCII.GetBytes("PING\\n".Replace("\\\\n", "\\n")));
        if (await ReadLineAsync(tls, CancellationToken.None) != "PONG") throw new IOException("Heartbeat recusado.");
    }
}
if (args.Length < 5 || (args[0] != "host" && args[0] != "join"))
{
    Console.WriteLine("Uso: dotnet RemotePortable.Tunnel.dll host|join RELAY_DNS PORTA_RELAY ID TOKEN [PORTA_LOCAL]");
    return;
}
var mode = args[0];
var relayHost = args[1];
var relayPort = int.Parse(args[2]);
var id = args[3];
var token = args[4];
var localPort = args.Length > 5 ? int.Parse(args[5]) : (mode == "host" ? 47890 : 47891);
if (token.Length < 32) throw new ArgumentException("TOKEN deve ter pelo menos 32 caracteres");
if (id.Length is < 6 or > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
    throw new ArgumentException("ID inválido");

async Task<SslStream> ConnectRelay(CancellationToken ct)
{
    var tcp = new TcpClient();
    await tcp.ConnectAsync(relayHost, relayPort, ct);
    var ssl = new SslStream(tcp.GetStream(), false);
    try
    {
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = relayHost }, ct);
        await ssl.WriteAsync(Encoding.ASCII.GetBytes($"{(mode == "host" ? "HOST" : "JOIN")} {token} {id}\n"), ct);
        var answer = await ReadLineAsync(ssl, ct);
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
    Console.WriteLine($"HOST: compartilhamento RemotePortable deve estar ativo em 127.0.0.1:{localPort}");
    while (!shutdown.IsCancellationRequested)
    {
        try
        {
            using var relay = await ConnectRelay(shutdown.Token);
            Console.WriteLine("Registrado no Relay. Aguardando controlador...");
            var answer = await ReadLineAsync(relay, shutdown.Token);
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
    Console.WriteLine($"JOIN: no RemotePortable, conecte a 127.0.0.1:{localPort} (ID {id})");
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
