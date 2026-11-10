using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RemotePortable.Shared;

internal sealed record OnlineDevice(string Id, string Name, string Platform, string Fingerprint);

internal static class RelayDirectory
{
    public static async Task<IReadOnlyList<OnlineDevice>> ListAsync(string relay, int port, CancellationToken ct = default)
    {
        var identity = DeviceIdentity.LoadOrCreate();
        await RelayClientAuth.EnsureApprovedAsync(relay, port, identity, ct);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(relay, port, ct);
        using var tls = new SslStream(tcp.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = relay }, ct);
        await RelayClientAuth.AuthenticateAsync(tls, identity, "LIST", "-", ct);
        var response = await RelayClientAuth.ReadLineAsync(tls, ct);
        if (response.StartsWith("ERR")) throw new IOException("Relay: " + response);
        using var doc = JsonDocument.Parse(response);
        return doc.RootElement.EnumerateArray().Select(x => new OnlineDevice(
            x.GetProperty("id").GetString() ?? "",
            x.GetProperty("name").GetString() ?? "",
            x.GetProperty("platform").GetString() ?? "",
            x.GetProperty("fingerprint").GetString() ?? "")).ToArray();
    }
}

internal sealed class RelayTunnelProcess : IDisposable
{
    readonly Process process;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event Action<string>? Message;
    public RelayTunnelProcess(string mode, string relay, int relayPort, string id, int localPort, string? fingerprint = null)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RemotePortable.Tunnel.exe" : "RemotePortable.Tunnel");
        if (!File.Exists(executable)) throw new FileNotFoundException("Coloque o executável RemotePortable.Tunnel na mesma pasta do aplicativo.", executable);
        var psi = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { mode, relay, relayPort.ToString(), id, localPort.ToString() })
            psi.ArgumentList.Add(value);
        if (mode == "host" && fingerprint != null) psi.ArgumentList.Add(fingerprint);
        process = Process.Start(psi) ?? throw new IOException("Não foi possível iniciar o Tunnel.");
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.StartsWith("JOIN:")) ready.TrySetResult();
                    Message?.Invoke(line);
                }
                ready.TrySetException(new IOException("O Tunnel terminou antes de abrir a porta local."));
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        });
        _ = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync() is { } line) Message?.Invoke(line); }
            catch { }
        });
        _ = process.WaitForExitAsync().ContinueWith(_ => ready.TrySetException(new IOException("O Tunnel foi encerrado.")));
    }
    public async Task WaitUntilReadyAsync(CancellationToken ct = default)
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
    }
    public void Dispose()
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
    }
}
