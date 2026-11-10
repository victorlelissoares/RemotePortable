using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using RemotePortable.Shared;

namespace RemotePortable.Linux;

internal sealed class MainWindow : Window
{
    readonly TextBox address = new() { Text = "127.0.0.1", Width = 170 };
    readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Value = 47890, Width = 95 };
    readonly TextBox password = new() { PasswordChar = '●', Width = 170, Watermark = "Senha (12+ caracteres)" };
    readonly TextBox fingerprint = new() { Width = 390, Watermark = "SHA-256 do certificado do host" };
    readonly NumericUpDown fps = new() { Minimum = 5, Maximum = 60, Value = 20, Width = 65 };
    readonly CheckBox allowInput = new() { Content = "Permitir controle", IsChecked = true };
    readonly Button host = new() { Content = "Compartilhar" };
    readonly Button connect = new() { Content = "Controlar" };
    readonly Button stop = new() { Content = "Encerrar", IsEnabled = false };
    readonly TextBlock status = new() { Text = "Pronto", TextWrapping = TextWrapping.Wrap };
    readonly Image image = new() { Stretch = Stretch.Uniform, Focusable = true };
    readonly Border viewer = new() { Background = Brushes.Black, Child = null };
    readonly TextBox relayHost = new() { Text = "remoteportable.cloud", Width = 180 };
    readonly TextBox relayToken = new() { PasswordChar = '●', Width = 200, Watermark = "Token Relay" };
    readonly TextBox deviceId = new() { Text = "linux-" + Environment.MachineName.ToLowerInvariant().Replace("_", "-"), Width = 180 };
    readonly ListBox devices = new() { Height = 130 };
    readonly Button refresh = new() { Content = "Atualizar" };
    readonly Button quickConnect = new() { Content = "Conectar selecionado" };
    readonly CheckBox useRelay = new() { Content = "Usar Relay", IsChecked = true };
    readonly DispatcherTimer directoryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    RelayTunnelProcess? managedTunnel;
    RemoteSession? session;
    WriteableBitmap? bitmap;
    int width, height;
    int frameQueued;
    public MainWindow()
    {
        Title = "RemotePortable | Linux (X11)";
        Width = 1100; Height = 760; MinWidth = 760; MinHeight = 520;
        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        void Add(string label, Control control)
        {
            toolbar.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 3, 0) });
            toolbar.Children.Add(control);
        }
        Add("IP", address); Add("Porta", port); Add("Senha", password);
        toolbar.Children.Add(host); toolbar.Children.Add(connect); toolbar.Children.Add(stop);
        toolbar.Children.Add(allowInput); Add("FPS", fps); Add("Fingerprint TLS", fingerprint);
        Add("Relay", relayHost); Add("Token", relayToken); Add("ID", deviceId);
        toolbar.Children.Add(useRelay);
        var discoveryPanel = new StackPanel { Margin = new Thickness(10), Spacing = 5 };
        discoveryPanel.Children.Add(new TextBlock { Text = "Computadores online" });
        discoveryPanel.Children.Add(devices);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(refresh); actions.Children.Add(quickConnect);
        discoveryPanel.Children.Add(actions);
        refresh.Click += async (_, _) => await RefreshDevices();
        quickConnect.Click += async (_, _) => await ConnectSelected();
        devices.DoubleTapped += async (_, _) => await ConnectSelected();
        directoryTimer.Tick += async (_, _) => { if (!string.IsNullOrWhiteSpace(relayToken.Text)) await RefreshDevices(); };
        directoryTimer.Start();
        var top = new ScrollViewer { Content = toolbar, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 135 };
        viewer.Child = image;
        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top); layout.Children.Add(top);
        DockPanel.SetDock(discoveryPanel, Dock.Top); layout.Children.Add(discoveryPanel);
        var bottom = new Border { Padding = new Thickness(12), Child = status };
        DockPanel.SetDock(bottom, Dock.Bottom); layout.Children.Add(bottom);
        layout.Children.Add(viewer);
        Content = layout;
        host.Click += async (_, _) => await Start(true);
        connect.Click += async (_, _) => await Start(false);
        stop.Click += async (_, _) => await EndSession();
        Closing += (_, _) => { directoryTimer.Stop(); session?.Stop(); managedTunnel?.Dispose(); };
        image.PointerMoved += (_, e) => Mouse(0, e.GetPosition(image));
        image.PointerPressed += (_, e) =>
        {
            image.Focus();
            var props = e.GetCurrentPoint(image).Properties;
            Mouse(props.IsLeftButtonPressed ? 1 : 3, e.GetPosition(image));
        };
        image.PointerReleased += (_, e) =>
        {
            var props = e.GetCurrentPoint(image).Properties;
            Mouse(props.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased ? 2 : 4, e.GetPosition(image));
        };
        image.PointerWheelChanged += (_, e) => Send($"W|{(int)(e.Delta.Y * 120)}");
        image.KeyDown += (_, e) => { var vk = VirtualKey(e.Key); if (vk != 0) { Send($"K|1|{vk}"); e.Handled = true; } };
        image.KeyUp += (_, e) => { var vk = VirtualKey(e.Key); if (vk != 0) { Send($"K|0|{vk}"); e.Handled = true; } };
    }
    static int VirtualKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return 65 + (key - Key.A);
        if (key >= Key.D0 && key <= Key.D9) return 48 + (key - Key.D0);
        return key switch { Key.Back => 8, Key.Tab => 9, Key.Enter => 13, Key.Escape => 27,
            Key.Space => 32, Key.Left => 37, Key.Up => 38, Key.Right => 39,
            Key.Down => 40, Key.Delete => 46, _ => 0 };
    }
    async Task RefreshDevices()
    {
        if (string.IsNullOrWhiteSpace(relayToken.Text)) return;
        try
        {
            var list = await RelayDirectory.ListAsync(relayHost.Text?.Trim() ?? "", 47900, relayToken.Text);
            devices.ItemsSource = list.Select(x => new DeviceChoice(x)).ToArray();
        }
        catch (Exception ex) { status.Text = "Descoberta: " + ex.Message; }
    }
    sealed record DeviceChoice(OnlineDevice Device)
    {
        public override string ToString() => $"{Device.Name} ({Device.Platform}) — {Device.Id}";
    }
    async Task ConnectSelected()
    {
        if (devices.SelectedItem is not DeviceChoice choice || session != null) return;
        try
        {
            managedTunnel?.Dispose();
            managedTunnel = new RelayTunnelProcess("join", relayHost.Text?.Trim() ?? "", 47900,
                choice.Device.Id, relayToken.Text ?? "", 47891);
            address.Text = "127.0.0.1";
            port.Value = 47891;
            if (choice.Device.Fingerprint.Length != 64 || choice.Device.Fingerprint.All(ch => ch == '0'))
                throw new InvalidOperationException("Host ainda não publicou fingerprint TLS válido.");
            fingerprint.Text = choice.Device.Fingerprint;
            if (string.IsNullOrWhiteSpace(password.Text))
            {
                status.Text = "Informe a senha do host antes de conectar.";
                managedTunnel?.Dispose(); managedTunnel = null;
                return;
            }
            await RelayTunnelProcess.WaitForLocalListenerAsync(47891);
            await Start(false);
        }
        catch (Exception ex) { status.Text = "Conexão: " + ex.Message; managedTunnel?.Dispose(); managedTunnel = null; }
    }
    async Task Start(bool isHost)
    {
        if (session != null) return;
        var current = new RemoteSession();
        session = current;
        host.IsEnabled = connect.IsEnabled = false; stop.IsEnabled = true;
        current.Status += message => Dispatcher.UIThread.Post(() =>
        {
            status.Text = message;
            if (message.StartsWith("Fingerprint SHA-256: "))
            {
                fingerprint.Text = message["Fingerprint SHA-256: ".Length..];
                if (useRelay.IsChecked == true)
                {
                    try
                    {
                        managedTunnel?.Dispose();
                        managedTunnel = new RelayTunnelProcess("host", relayHost.Text?.Trim() ?? "", 47900,
                            deviceId.Text?.Trim() ?? "", relayToken.Text ?? "", (int)(port.Value ?? 47890), fingerprint.Text);
                    }
                    catch (Exception ex) { status.Text = "Relay: " + ex.Message; }
                }
            }
        });
        current.Approve = peer =>
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                var dialog = new Window { Title = "Autorizar acesso remoto", Width = 400, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                var yes = new Button { Content = "Autorizar" };
                var no = new Button { Content = "Recusar" };
                yes.Click += (_, _) => dialog.Close(true);
                no.Click += (_, _) => dialog.Close(false);
                dialog.Content = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = {
                    new TextBlock { Text = $"Permitir que {peer} controle este computador?", TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { yes, no } }
                }};
                try { result.TrySetResult(await dialog.ShowDialog<bool>(this)); }
                catch { result.TrySetResult(false); }
            });
            return result.Task;
        };
        current.Frame += (pixels, w, h) =>
        {
            if (Interlocked.Exchange(ref frameQueued, 1) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                try { Display(pixels, w, h); }
                finally { Interlocked.Exchange(ref frameQueued, 0); }
            }, DispatcherPriority.Background);
        };
        try
        {
            if (isHost)
                await Task.Run(() => current.HostAsync((int)(port.Value ?? 47890), password.Text ?? "", (int)(fps.Value ?? 20), allowInput.IsChecked == true));
            else
                await Task.Run(() => current.ConnectAsync(address.Text?.Trim() ?? "", (int)(port.Value ?? 47890), password.Text ?? "", fingerprint.Text ?? ""));
        }
        catch (Exception ex) { status.Text = "Erro: " + ex.Message; }
        finally
        {
            await current.DisposeAsync();
            managedTunnel?.Dispose(); managedTunnel = null;
            if (ReferenceEquals(session, current)) session = null;
            host.IsEnabled = connect.IsEnabled = true; stop.IsEnabled = false;
        }
    }
    async Task EndSession()
    {
        session?.Stop();
        await Task.CompletedTask;
    }
    void Display(byte[] pixels, int w, int h)
    {
        if (bitmap == null || width != w || height != h)
        {
            bitmap?.Dispose();
            width = w; height = h;
            bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            image.Source = bitmap;
        }
        using var locked = bitmap.Lock();
        int rowSize = w * 4;
        for (int y = 0; y < h; y++)
            Marshal.Copy(pixels, y * rowSize, IntPtr.Add(locked.Address, y * locked.RowBytes), rowSize);
    }
    void Mouse(int kind, Point point)
    {
        if (session == null || width < 1 || height < 1 || image.Bounds.Width <= 0 || image.Bounds.Height <= 0) return;
        double scale = Math.Min(image.Bounds.Width / width, image.Bounds.Height / height);
        double ox = (image.Bounds.Width - width * scale) / 2, oy = (image.Bounds.Height - height * scale) / 2;
        int x = (int)((point.X - ox) / scale), y = (int)((point.Y - oy) / scale);
        if (x >= 0 && x < width && y >= 0 && y < height) Send($"M|{kind}|{x}|{y}");
    }
    void Send(string cmd)
    {
        var current = session;
        if (current != null && current.Width > 0)
            _ = current.SendAsync(cmd).ContinueWith(t => { if (t.Exception != null) Dispatcher.UIThread.Post(() => status.Text = t.Exception.GetBaseException().Message); }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
