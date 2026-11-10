using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RemotePortable.Linux;

internal sealed class RemoteSession : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim sendLock = new(1, 1);
    TcpListener? listener;
    TcpClient? socket;
    Stream? stream;
    Process? ffmpeg;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public event Action<string>? Status;
    public event Action<byte[], int, int>? Frame;
    public Func<string, Task<bool>> Approve { get; set; } = _ => Task.FromResult(false);
    void Report(string s) => Status?.Invoke(s);

    public void Stop()
    {
        stop.Cancel();
        listener?.Stop();
        socket?.Dispose();
        try { ffmpeg?.Kill(entireProcessTree: true); } catch { }
    }

    public async Task HostAsync(int port, string password, int fps, bool allowInput)
    {
        if (password.Length < 12) throw new ArgumentException("A senha deve ter pelo menos 12 caracteres.");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("Host requer sessão gráfica X11 (DISPLAY). Wayland nativo não é suportado.");
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=RemotePortable", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        Report("Fingerprint SHA-256: " + Convert.ToHexString(SHA256.HashData(cert.RawData)));
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Report($"Aguardando conexão na porta {port}");
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                socket = client;
                try
                {
                    using var tls = new SslStream(client.GetStream(), false);
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                        ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, stop.Token);
                    using var reader = new BinaryReader(tls, Encoding.UTF8, true);
                    using var writer = new BinaryWriter(tls, Encoding.UTF8, true);
                    var attempt = reader.ReadString();
                    bool correct = CryptographicOperations.FixedTimeEquals(
                        SHA256.HashData(Encoding.UTF8.GetBytes(attempt)),
                        SHA256.HashData(Encoding.UTF8.GetBytes(password)));
                    bool approved = correct && await Approve(client.Client.RemoteEndPoint?.ToString() ?? "desconhecido");
                    writer.Write(approved); writer.Flush();
                    if (!approved) { Report("Acesso recusado"); continue; }
                    stream = tls;
                    await ServeAsync(tls, writer, fps, allowInput);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex) { Report("Sessão encerrada: " + ex.Message); }
                finally { stream = null; socket = null; }
                if (!stop.IsCancellationRequested) Report($"Aguardando conexão na porta {port}");
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }

    async Task ServeAsync(Stream tls, BinaryWriter writer, int fps, bool allowInput)
    {
        var display = Environment.GetEnvironmentVariable("DISPLAY")!;
        var size = await RunCommandAsync("xdpyinfo", Array.Empty<string>());
        var dimensions = System.Text.RegularExpressions.Regex.Match(size, @"dimensions:\s*(\d+)x(\d+)");
        if (!dimensions.Success) throw new InvalidOperationException("Não foi possível detectar a resolução X11 (xdpyinfo).");
        int width = int.Parse(dimensions.Groups[1].Value), height = int.Parse(dimensions.Groups[2].Value);
        if (width is < 1 or > 16384 || height is < 1 or > 16384) throw new InvalidDataException("Resolução inválida.");
        writer.Write(width); writer.Write(height); writer.Write(1); writer.Flush();
        var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-f", "x11grab", "-framerate", fps.ToString(), "-video_size", $"{width}x{height}", "-i", display + "+0,0", "-an", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-pix_fmt", "yuv420p", "-f", "mpegts", "pipe:1" })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new IOException("Falha ao iniciar ffmpeg");
        ffmpeg = process;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var input = Task.Run(async () =>
        {
            using var inputReader = new BinaryReader(tls, Encoding.UTF8, true);
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    string cmd = inputReader.ReadString();
                    if (cmd.Length > 120) throw new InvalidDataException("Comando excessivo.");
                    if (allowInput) await ExecuteInputAsync(cmd, width, height, linked.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException) { linked.Cancel(); }
        });
        var buffer = new byte[32768];
        try
        {
            Report("Sessão ativa");
            while (!linked.IsCancellationRequested)
            {
                int n = await process.StandardOutput.BaseStream.ReadAsync(buffer, linked.Token);
                if (n == 0) throw new IOException("Captura FFmpeg encerrada: " + await process.StandardError.ReadToEndAsync());
                writer.Write(n); writer.Write(buffer, 0, n); writer.Flush();
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            linked.Cancel();
            try { process.Kill(entireProcessTree: true); } catch { }
            try { tls.Dispose(); } catch { }
            try { await input; } catch { }
            ffmpeg = null;
        }
    }

    public async Task ConnectAsync(string host, int port, string password, string fingerprint)
    {
        var expected = fingerprint.Replace(":", "").Trim().ToUpperInvariant();
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new ArgumentException("Fingerprint SHA-256 inválido.");
        using var client = new TcpClient();
        socket = client;
        try
        {
            await client.ConnectAsync(host, port, stop.Token);
            using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
                cert != null && CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(cert.GetRawCertData()), Convert.FromHexString(expected)));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "RemotePortable", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, stop.Token);
            stream = tls;
            using var reader = new BinaryReader(tls, Encoding.UTF8, true);
            using var writer = new BinaryWriter(tls, Encoding.UTF8, true);
            writer.Write(password); writer.Flush();
            if (!reader.ReadBoolean()) throw new UnauthorizedAccessException("Senha incorreta ou acesso recusado pelo host.");
            int w = reader.ReadInt32(), h = reader.ReadInt32(), format = reader.ReadInt32();
            if (w is < 1 or > 16384 || h is < 1 or > 16384 || format != 1) throw new InvalidDataException("Cabeçalho de vídeo inválido.");
            Width = w; Height = h;
            Report($"Conectado: {w}x{h}");
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer", "-flags", "low_delay", "-f", "mpegts", "-i", "pipe:0", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
                psi.ArgumentList.Add(a);
            using var process = Process.Start(psi) ?? throw new IOException("Falha ao iniciar FFmpeg");
            ffmpeg = process;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            var pump = Task.Run(async () =>
            {
                try
                {
                    while (!linked.IsCancellationRequested)
                    {
                        int len = reader.ReadInt32();
                        if (len is < 1 or > 32768) throw new InvalidDataException("Pacote inválido.");
                        var bytes = reader.ReadBytes(len);
                        if (bytes.Length != len) throw new EndOfStreamException();
                        await process.StandardInput.BaseStream.WriteAsync(bytes, linked.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException or OperationCanceledException or ObjectDisposedException) { linked.Cancel(); }
                finally { try { process.StandardInput.Close(); } catch { } }
            });
            int frameSize = checked(w * h * 4);
            var pixels = new byte[frameSize];
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    int offset = 0;
                    while (offset < frameSize)
                    {
                        int n = await process.StandardOutput.BaseStream.ReadAsync(pixels.AsMemory(offset, frameSize - offset), linked.Token);
                        if (n == 0) throw new IOException("Decoder FFmpeg encerrado.");
                        offset += n;
                    }
                    Frame?.Invoke((byte[])pixels.Clone(), w, h);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally
            {
                linked.Cancel();
                try { process.Kill(entireProcessTree: true); } catch { }
                try { tls.Dispose(); } catch { }
                try { await pump; } catch { }
                ffmpeg = null;
            }
        }
        finally { stream = null; socket = null; }
    }

    public async Task SendAsync(string command)
    {
        if (stream == null || stop.IsCancellationRequested) return;
        await sendLock.WaitAsync(stop.Token);
        try { new BinaryWriter(stream, Encoding.UTF8, true).Write(command); await stream.FlushAsync(stop.Token); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Stop(); }
        finally { sendLock.Release(); }
    }

    static async Task<string> RunCommandAsync(string command, string[] args)
    {
        var psi = new ProcessStartInfo(command) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi) ?? throw new IOException("Não foi possível iniciar " + command);
        string output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new IOException(command + " retornou " + p.ExitCode);
        return output;
    }

    static async Task ExecuteInputAsync(string cmd, int w, int h, CancellationToken ct)
    {
        var parts = cmd.Split('|');
        string[]? args = null;
        if (parts.Length == 4 && parts[0] == "M" && int.TryParse(parts[1], out var kind) &&
            int.TryParse(parts[2], out var x) && int.TryParse(parts[3], out var y) &&
            x >= 0 && x < w && y >= 0 && y < h)
        {
            args = kind switch {
                0 => new[] { "mousemove", "--sync", x.ToString(), y.ToString() },
                1 => new[] { "mousedown", "1" }, 2 => new[] { "mouseup", "1" },
                3 => new[] { "mousedown", "3" }, 4 => new[] { "mouseup", "3" }, _ => null
            };
            if (kind is 1 or 3)
                await RunCommandAsync("xdotool", new[] { "mousemove", "--sync", x.ToString(), y.ToString() });
        }
        else if (parts.Length == 2 && parts[0] == "W" && int.TryParse(parts[1], out var delta) && Math.Abs((long)delta) <= 1200)
            args = new[] { "click", delta > 0 ? "4" : "5" };
        else if (parts.Length == 3 && parts[0] == "K" && (parts[1] == "0" || parts[1] == "1") &&
                 byte.TryParse(parts[2], out var key))
        {
            // Windows VK values mapped to X11 keysym for cross-platform interoperability.
            var keysym = key switch {
                8 => "BackSpace", 9 => "Tab", 13 => "Return", 27 => "Escape", 32 => "space",
                37 => "Left", 38 => "Up", 39 => "Right", 40 => "Down", 46 => "Delete",
                >= 65 and <= 90 => ((char)(key + 32)).ToString(),
                >= 48 and <= 57 => ((char)key).ToString(), _ => null
            };
            if (keysym != null) args = new[] { parts[1] == "1" ? "keydown" : "keyup", keysym };
        }
        if (args == null) return;
        ct.ThrowIfCancellationRequested();
        await RunCommandAsync("xdotool", args);
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        stop.Dispose();
        sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
