using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemotePortable.Shared;

internal sealed record OnlineDevice(string Id, string Name, string Platform, string Fingerprint);

internal static class RelayDirectory
{
    public static async Task<IReadOnlyList<OnlineDevice>> ListAsync(string relay, int port, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Informe o token do Relay.");
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(relay, port, ct);
        using var tls = new SslStream(tcp.GetStream(), false);
        await tls.AuthenticateAsClientAsync(relay);
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"LIST {token} list-all\n"), ct);
        var response = await ReadLineAsync(tls, ct);
        if (response.StartsWith("ERR")) throw new IOException("Relay: " + response);
        using var doc = JsonDocument.Parse(response);
        return doc.RootElement.EnumerateArray().Select(x => new OnlineDevice(
            x.GetProperty("id").GetString() ?? "",
            x.GetProperty("name").GetString() ?? "",
            x.GetProperty("platform").GetString() ?? "",
            x.GetProperty("fingerprint").GetString() ?? "")).ToArray();
    }
    static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 65536)
        {
            if (await stream.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
            if (one[0] == 10) return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            buffer.WriteByte(one[0]);
        }
        throw new InvalidDataException("Resposta muito longa.");
    }
}

internal sealed class RelayTunnelProcess : IDisposable
{
    readonly Process process;
    public RelayTunnelProcess(string mode, string relay, int relayPort, string id, string token, int localPort, string? fingerprint = null)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RemotePortable.Tunnel.exe" : "RemotePortable.Tunnel");
        if (!File.Exists(executable)) throw new FileNotFoundException("Coloque o executável RemotePortable.Tunnel na mesma pasta do aplicativo.", executable);
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = false, RedirectStandardError = false };
        foreach (var value in new[] { mode, relay, relayPort.ToString(), id, token, localPort.ToString() })
            psi.ArgumentList.Add(value);
        if (mode == "host" && fingerprint != null) psi.ArgumentList.Add(fingerprint);
        process = Process.Start(psi) ?? throw new IOException("Não foi possível iniciar o Tunnel.");
    }
    public static async Task WaitForLocalListenerAsync(int port, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync("127.0.0.1", port, ct);
                return;
            }
            catch (SocketException) { await Task.Delay(100, ct); }
        }
        throw new TimeoutException("O Tunnel não abriu a porta local a tempo.");
    }
    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }
}
