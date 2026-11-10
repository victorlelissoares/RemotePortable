using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using System.Windows.Forms;
using RemotePortable.Shared;

namespace RemotePortable;

internal static class Program
{
    [STAThread] static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new MainForm()); }
}

internal sealed class MainForm : Form
{
    readonly TextBox ip = new() { Text = "127.0.0.1", Width = 160 };
    readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Value = 47890, Width = 85 };
    readonly TextBox secret = new() { Width = 175, UseSystemPasswordChar = true };
    readonly TextBox fingerprint = new() { Width = 420, PlaceholderText = "SHA-256 do certificado do host (obrigatório)" };
    readonly Label status = new() { AutoSize = true, Text = "Pronto" };
    readonly Button hostButton = new() { Text = "Compartilhar", AutoSize = true };
    readonly Button connectButton = new() { Text = "Controlar", AutoSize = true };
    readonly Button stopButton = new() { Text = "Encerrar", AutoSize = true, Enabled = false };
    readonly NumericUpDown fps = new() { Minimum = 5, Maximum = 60, Value = 30, Width = 60 };
    readonly CheckBox allowInput = new() { Text = "Permitir mouse e teclado", Checked = true, AutoSize = true };
    readonly PictureBox screen = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(28, 31, 38), TabStop = true };
    readonly TextBox relayHost = new() { Text = "remoteportable.cloud", Width = 170 };
    readonly TextBox relayToken = new() { Width = 180, UseSystemPasswordChar = true, PlaceholderText = "Token do Relay" };
    readonly TextBox deviceId = new() { Text = "win-" + Environment.MachineName.ToLowerInvariant().Replace("_", "-"), Width = 155 };
    readonly ListBox devices = new() { Width = 320, Height = 120, Dock = DockStyle.Fill };
    readonly Button refresh = new() { Text = "Atualizar", AutoSize = true };
    readonly Button quickConnect = new() { Text = "Conectar selecionado", AutoSize = true };
    readonly CheckBox useRelay = new() { Text = "Usar Relay", Checked = true, AutoSize = true };
    readonly System.Windows.Forms.Timer discoveryTimer = new() { Interval = 10000 };
    RelayTunnelProcess? managedTunnel;
    CancellationTokenSource? session;
    TcpListener? listener;
    TcpClient? active;
    Stream? connection;
    readonly object writeLock = new();
    int remoteW, remoteH;
    bool remoteInput;
    string? certHash;
    readonly object lifecycleLock = new();
    int hostGeneration;
    bool stopping;
    static readonly string LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemotePortable", "remoteportable.log");
    static void Log(string message)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); lock (typeof(MainForm)) File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } catch { }
    }
    void OnUi(Action action) { if (!IsDisposed && IsHandleCreated) BeginInvoke(action); }

    public MainForm()
    {
        Text = "RemotePortable | Windows"; Width = 1050; Height = 730; MinimumSize = new Size(760, 500);
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 118, Padding = new Padding(10), AutoScroll = true, WrapContents = true };
        top.Controls.AddRange(new Control[] { new Label { Text = "IP:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, ip,
            new Label { Text = "Porta:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, port,
            new Label { Text = "Senha:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, secret,
            hostButton, connectButton, stopButton, allowInput,
            new Label { Text = "FPS:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, fps,
            new Label { Text = "Fingerprint TLS:", AutoSize = true, Margin = new Padding(8, 8, 3, 3) }, fingerprint });
        top.Controls.AddRange(new Control[] { new Label { Text = "Relay:", AutoSize = true }, relayHost,
            new Label { Text = "Token:", AutoSize = true }, relayToken,
            new Label { Text = "Meu ID:", AutoSize = true }, deviceId, useRelay });
        var directory = new Panel { Dock = DockStyle.Left, Width = 330, Padding = new Padding(8) };
        var directoryActions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40 };
        directoryActions.Controls.AddRange(new Control[] { refresh, quickConnect });
        directory.Controls.Add(devices); directory.Controls.Add(directoryActions);
        refresh.Click += async (_, _) => await RefreshDevices();
        quickConnect.Click += async (_, _) => await ConnectSelected();
        devices.DoubleClick += async (_, _) => await ConnectSelected();
        discoveryTimer.Tick += async (_, _) => { if (!string.IsNullOrWhiteSpace(relayToken.Text)) await RefreshDevices(); };
        discoveryTimer.Start();
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(12) };
        status.Dock = DockStyle.Fill; bottom.Controls.Add(status);
        Controls.Add(screen); Controls.Add(directory); Controls.Add(bottom); Controls.Add(top);
        hostButton.Click += async (_, _) => await StartHost();
        connectButton.Click += async (_, _) => await StartClient();
        stopButton.Click += (_, _) => Stop();
        FormClosing += (_, _) => { discoveryTimer.Stop(); Stop(); };
        screen.MouseMove += (_, e) => SendMouse(0, e.Location);
        screen.MouseDown += (_, e) => { screen.Focus(); SendMouse(e.Button == MouseButtons.Left ? 1 : 3, e.Location); };
        screen.MouseUp += (_, e) => SendMouse(e.Button == MouseButtons.Left ? 2 : 4, e.Location);
        screen.MouseWheel += (_, e) => SendInputMessage($"W|{e.Delta}");
        screen.KeyDown += (_, e) => { SendInputMessage($"K|1|{(int)e.KeyCode}"); e.Handled = true; e.SuppressKeyPress = true; };
        screen.KeyUp += (_, e) => { SendInputMessage($"K|0|{(int)e.KeyCode}"); e.Handled = true; e.SuppressKeyPress = true; };
    }

    sealed record DeviceChoice(OnlineDevice Device)
    {
        public override string ToString() => $"{Device.Name} ({Device.Platform}) — {Device.Id}";
    }
    async Task RefreshDevices()
    {
        if (string.IsNullOrWhiteSpace(relayToken.Text)) return;
        try
        {
            var list = await RelayDirectory.ListAsync(relayHost.Text.Trim(), 47900, relayToken.Text);
            devices.Items.Clear();
            foreach (var device in list) devices.Items.Add(new DeviceChoice(device));
        }
        catch (Exception ex) { status.Text = "Descoberta: " + ex.Message; }
    }
    async Task ConnectSelected()
    {
        if (devices.SelectedItem is not DeviceChoice choice || session is { IsCancellationRequested: false }) return;
        try
        {
            managedTunnel?.Dispose();
            managedTunnel = new RelayTunnelProcess("join", relayHost.Text.Trim(), 47900, choice.Device.Id, relayToken.Text, 47891);
            ip.Text = "127.0.0.1";
            port.Value = 47891;
            if (choice.Device.Fingerprint.Length != 64 || choice.Device.Fingerprint.All(ch => ch == '0'))
                throw new InvalidOperationException("Host ainda não publicou fingerprint TLS válido.");
            fingerprint.Text = choice.Device.Fingerprint;
            await Task.Delay(350);
            if (string.IsNullOrWhiteSpace(secret.Text))
            {
                MessageBox.Show("Informe a senha do host antes de conectar.", "Autenticação");
                managedTunnel?.Dispose(); managedTunnel = null;
                return;
            }
            await StartClient();
        }
        catch (Exception ex) { status.Text = "Conexão: " + ex.Message; managedTunnel?.Dispose(); managedTunnel = null; }
    }
    void SetStatus(string s) { Log(s); OnUi(() => status.Text = s); }
    void Busy(bool b) { hostButton.Enabled = connectButton.Enabled = !b; stopButton.Enabled = b; }
    void Stop()
    {
        Log($"Stop solicitado. Thread={Environment.CurrentManagedThreadId}; origem={Environment.StackTrace}");
        stopping = true;
        managedTunnel?.Dispose(); managedTunnel = null;
        Interlocked.Increment(ref hostGeneration);
        session?.Cancel();
        try { connection?.Dispose(); } catch { }
        try { active?.Close(); } catch { }
        try { listener?.Stop(); } catch { }
        connection = null; active = null; listener = null; remoteInput = false;
        Busy(false); status.Text = "Desconectado";
        Log("Compartilhamento encerrado.");
    }

    Task StartHost()
    {
        if (secret.Text.Length < 12) { MessageBox.Show("Use uma senha de pelo menos 12 caracteres."); return Task.CompletedTask; }
        stopping = false;
        Busy(true);
        session = new CancellationTokenSource();
        var token = session.Token;
        string password = secret.Text;
        int hostPort = (int)port.Value;
        int hostFps = (int)fps.Value;
        // O servidor executa fora da thread WinForms: leitura TLS e espera por clientes são bloqueantes.
        int generation = Interlocked.Increment(ref hostGeneration);
        Log($"Iniciando compartilhamento, geração {generation}, porta {hostPort}.");
        _ = Task.Run(() => HostSupervisor(password, hostPort, hostFps, token, generation));
        return Task.CompletedTask;
    }

    async Task HostSupervisor(string password, int hostPort, int hostFps, CancellationToken token, int generation)
    {
        try
        {
            using RSA rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=RemotePortable", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            certHash = Convert.ToHexString(SHA256.HashData(cert.RawData));
            OnUi(() => fingerprint.Text = certHash);
            if (useRelay.Checked)
            {
                var relay = relayHost.Text.Trim();
                var tokenValue = relayToken.Text;
                var idValue = deviceId.Text.Trim();
                if (string.IsNullOrWhiteSpace(tokenValue)) throw new ArgumentException("Informe o token do Relay.");
                managedTunnel?.Dispose();
                managedTunnel = new RelayTunnelProcess("host", relay, 47900, idValue, tokenValue, hostPort, certHash);
            }
            int failures = 0;
            while (!token.IsCancellationRequested && generation == Volatile.Read(ref hostGeneration))
            {
                TcpListener? server = null;
                try
                {
                    server = new TcpListener(IPAddress.Any, hostPort);
                    server.Start();
                    listener = server;
                    failures = 0;
                    Log($"Listener iniciado em 0.0.0.0:{hostPort}, PID {Environment.ProcessId}, geração {generation}.");
                    SetStatus($"Aguardando conexão na porta {hostPort}.");
                    await AcceptClients(server, cert, password, hostPort, hostFps, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                        throw new IOException("Loop de aceitação terminou inesperadamente.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failures++;
                    Log($"Listener interrompido (tentativa {failures}): {ex}");
                    SetStatus($"Reconectando listener na porta {hostPort}...");
                }
                finally
                {
                    try { server?.Stop(); } catch { }
                    if (ReferenceEquals(listener, server)) listener = null;
                    Log($"Listener finalizado; cancelado={token.IsCancellationRequested}; geração={generation}.");
                }
                if (!token.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, failures * 2))), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { Log($"Erro fatal no supervisor: {ex}"); SetStatus("Erro no servidor: " + ex.Message); }
        finally
        {
            Log($"Supervisor finalizado, geração={generation}, cancelado={token.IsCancellationRequested}.");
            if (!token.IsCancellationRequested && generation == Volatile.Read(ref hostGeneration))
                OnUi(() => { if (!stopping && generation == Volatile.Read(ref hostGeneration)) Stop(); });
        }
    }

    async Task AcceptClients(TcpListener server, X509Certificate2 cert, string password, int hostPort, int hostFps, CancellationToken token)
    {
            while (!token.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await server.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (ObjectDisposedException) when (token.IsCancellationRequested) { break; }
                active = socket;
                Log($"Conexão recebida de {socket.Client.RemoteEndPoint}");
                try
                {
                    using var ssl = new SslStream(socket.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token).ConfigureAwait(false);
                    using var reader = new BinaryReader(ssl, Encoding.UTF8, true);
                    using var writer = new BinaryWriter(ssl, Encoding.UTF8, true);
                    var attempt = reader.ReadString();
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(attempt)), SHA256.HashData(Encoding.UTF8.GetBytes(password))))
                    {
                        writer.Write(false); writer.Flush(); SetStatus("Senha incorreta.");
                        await Task.Delay(1000, token).ConfigureAwait(false); continue;
                    }
                    var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    OnUi(() => approval.TrySetResult(MessageBox.Show(this, $"Autorizar acesso remoto de {socket.Client.RemoteEndPoint}?", "Solicitação de acesso", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes));
                    bool approved = await approval.Task.WaitAsync(token).ConfigureAwait(false);
                    writer.Write(approved); writer.Flush();
                    if (!approved) { SetStatus("Acesso recusado. Aguardando outra conexão."); continue; }
                    connection = ssl;
                    SetStatus("Sessão ativa. Clique em Encerrar para desconectar.");
                    await HostSession(ssl, hostFps, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex) { Log($"Falha na sessão: {ex}"); SetStatus("Sessão encerrada: " + ex.Message); }
                finally
                {
                    connection = null;
                    socket.Dispose();
                    active = null;
                    if (!token.IsCancellationRequested) SetStatus($"Aguardando nova conexão na porta {hostPort}.");
                }
            }
    }

    async Task HostSession(Stream stream, int hostFps, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var receiver = Task.Run(() =>
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            try { while (!linked.IsCancellationRequested) { var cmd = reader.ReadString(); if (cmd.Length < 120 && (bool)Invoke(() => allowInput.Checked)) InputDispatcher.Execute(cmd); } }
            catch { linked.Cancel(); }
        }, linked.Token);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        try
        {
            var bounds = Screen.PrimaryScreen!.Bounds;
            // Header: dimensions and video format (1 = MPEG-TS/H.264).
            writer.Write(bounds.Width); writer.Write(bounds.Height); writer.Write(1); writer.Flush();
            await using var capture = VideoPipeline.StartEncoder(hostFps, linked.Token);
            byte[] buffer = new byte[32768];
            while (!linked.IsCancellationRequested)
            {
                int read = await capture.Output.ReadAsync(buffer, linked.Token);
                if (read == 0) throw new IOException("Codificador de vídeo encerrado: " + capture.Diagnostics);
                writer.Write(read); writer.Write(buffer, 0, read); writer.Flush();
            }
        }
        catch (OperationCanceledException) { }
        finally { linked.Cancel(); try { stream.Dispose(); } catch { } try { await receiver; } catch { } }
    }

    async Task StartClient()
    {
        if (string.IsNullOrWhiteSpace(fingerprint.Text) || fingerprint.Text.Trim().Length != 64) { MessageBox.Show("Informe o fingerprint SHA-256 do host (64 caracteres hexadecimais)."); return; }
        if (string.IsNullOrEmpty(secret.Text)) { MessageBox.Show("Informe a senha."); return; }
        Busy(true); session = new CancellationTokenSource(); var token = session.Token;
        try
        {
            active = new TcpClient(); await active.ConnectAsync(ip.Text.Trim(), (int)port.Value, token);
            string expected = fingerprint.Text.Trim().Replace(":", "").ToUpperInvariant();
            using var ssl = new SslStream(active.GetStream(), false, (_, certificate, _, _) => certificate != null &&
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))), Encoding.ASCII.GetBytes(expected)));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "RemotePortable", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token);
            connection = ssl;
            using var writer = new BinaryWriter(ssl, Encoding.UTF8, true);
            using var reader = new BinaryReader(ssl, Encoding.UTF8, true);
            writer.Write(secret.Text); writer.Flush();
            if (!reader.ReadBoolean()) throw new UnauthorizedAccessException("Senha incorreta ou acesso recusado.");
            remoteInput = true; SetStatus("Conectado. Clique na imagem para controlar; Encerrar desconecta.");
            int w = reader.ReadInt32(), h = reader.ReadInt32(), format = reader.ReadInt32();
            if (w < 1 || h < 1 || w > 16384 || h > 16384 || format != 1)
                throw new InvalidDataException("Formato de vídeo inválido.");
            remoteW = w; remoteH = h;
            await using var decoder = VideoPipeline.StartDecoder(w, h, token);
            var pump = Task.Run(async () =>
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int len = reader.ReadInt32();
                        if (len < 1 || len > 32768) throw new InvalidDataException("Pacote inválido");
                        var bytes = reader.ReadBytes(len);
                        if (bytes.Length != len) throw new EndOfStreamException();
                        await decoder.Input!.WriteAsync(bytes, token);
                        await decoder.Input.FlushAsync(token);
                    }
                }
                finally { decoder.Input?.Dispose(); }
            }, token);
            int frameSize = checked(w * h * 4);
            byte[] pixels = new byte[frameSize];
            while (!token.IsCancellationRequested)
            {
                int offset = 0;
                while (offset < frameSize)
                {
                    int n = await decoder.Output.ReadAsync(pixels.AsMemory(offset, frameSize - offset), token);
                    if (n == 0) throw new IOException("Decodificador encerrado: " + decoder.Diagnostics);
                    offset += n;
                }
                var frame = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data = frame.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, frame.PixelFormat);
                try { Marshal.Copy(pixels, 0, data.Scan0, frameSize); }
                finally { frame.UnlockBits(data); }
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke(() => { var old = screen.Image; screen.Image = frame; old?.Dispose(); });
                else frame.Dispose();
            }
            await pump;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus("Conexão encerrada: " + ex.Message); }
        finally { if (!IsDisposed) BeginInvoke(() => Stop()); }
    }

    void SendMouse(int kind, Point p)
    {
        if (!remoteInput || remoteW == 0 || remoteH == 0 || screen.Image == null) return;
        double scale = Math.Min((double)screen.ClientSize.Width / remoteW, (double)screen.ClientSize.Height / remoteH);
        double offsetX = (screen.ClientSize.Width - remoteW * scale) / 2;
        double offsetY = (screen.ClientSize.Height - remoteH * scale) / 2;
        int x = (int)((p.X - offsetX) / scale), y = (int)((p.Y - offsetY) / scale);
        if (x < 0 || y < 0 || x >= remoteW || y >= remoteH) return;
        SendInputMessage($"M|{kind}|{x}|{y}");
    }
    void SendInputMessage(string command)
    {
        if (!remoteInput || connection == null) return;
        try { lock (writeLock) { var writer = new BinaryWriter(connection, Encoding.UTF8, true); writer.Write(command); writer.Flush(); } }
        catch { remoteInput = false; }
    }
}

internal static class InputDispatcher
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
    public static void Execute(string cmd)
    {
        var p = cmd.Split('|');
        if (p.Length == 4 && p[0] == "M" && int.TryParse(p[1], out int kind) && int.TryParse(p[2], out int x) && int.TryParse(p[3], out int y))
        {
            var bounds = Screen.PrimaryScreen!.Bounds;
            if (x < 0 || y < 0 || x >= bounds.Width || y >= bounds.Height) return;
            SetCursorPos(bounds.X + x, bounds.Y + y);
            uint flag = kind switch { 1 => 0x0002u, 2 => 0x0004u, 3 => 0x0008u, 4 => 0x0010u, _ => 0u };
            if (flag != 0) mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
        }
        else if (p.Length == 3 && p[0] == "K" && byte.TryParse(p[2], out byte key) && (p[1] == "1" || p[1] == "0")) keybd_event(key, 0, p[1] == "0" ? 2u : 0u, UIntPtr.Zero);
        else if (p.Length == 2 && p[0] == "W" && int.TryParse(p[1], out int delta) && Math.Abs((long)delta) <= 1200) mouse_event(0x0800, 0, 0, unchecked((uint)delta), UIntPtr.Zero);
    }
}
