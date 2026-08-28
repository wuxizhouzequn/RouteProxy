using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RouteProxy;

public partial class MainWindow : Window
{
    private readonly SingBoxProcess _core = new();
    private bool _allowClose;
    private bool _restoring;
    private bool _refreshing;
    private int _logLineCount;
    private IWebProxy? _activeNormalProxy;
    private bool _boundPhysicalAdapter;
    private string _activeUpstreamFingerprint = "";
    private CancellationTokenSource? _networkChangeDebounce;
    private bool _restartingForNetworkChange;
    private string _lastCheapSignal = "";
    private readonly DispatcherTimer _upstreamWatchdog = new() { Interval = TimeSpan.FromSeconds(10) };

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings(SettingsStore.Load());
        _core.LogLine += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        _core.Exited += () => Dispatcher.BeginInvoke(() => SetRunningState(false));
        _upstreamWatchdog.Tick += UpstreamWatchdog_Tick;
        NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
        AppendLog("就绪。请先连接作为上游的第三方 VPN；避免同时运行无关的第三个 TUN 工具。");
    }

    private async void UpstreamWatchdog_Tick(object? sender, EventArgs e)
    {
        if (!_core.IsRunning || _restoring || _restartingForNetworkChange)
            return;
        var signal = GetCheapNetworkSignal();
        if (signal == _lastCheapSignal && IsPacIntact())
            return;
        _lastCheapSignal = signal;
        await RebuildForNetworkChangeAsync();
    }

    private string GetCheapNetworkSignal()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            var proxy = $"{key?.GetValue("ProxyEnable")}|{key?.GetValue("ProxyServer")}|{key?.GetValue("AutoConfigURL")}";
            var adapters = string.Join(",", NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Select(adapter => adapter.Name + ":" + string.Join("+", adapter.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(address => address.Address.ToString()))));
            return proxy + "||" + adapters;
        }
        catch
        {
            return "";
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ReadSettings();
            ConfigureUpstream(settings, requireSystemProxy: settings.UpstreamMode == "system");
            SettingsStore.Save(settings);
            SetBusy(true);
            AppendLog("配置已保存，密码已使用 Windows 当前用户凭据加密。");
            await _core.StartAsync(settings);
            _activeUpstreamFingerprint = GetUpstreamFingerprint(settings);
            _lastCheapSignal = GetCheapNetworkSignal();
            SetRunningState(true);
            await RefreshIpAsync();
        }
        catch (Exception exception)
        {
            AppendLog("启动失败：" + exception.Message);
            SetRunningState(false);
            MessageBox.Show(this, exception.Message, "无法开启分流", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            await _core.StopAsync();
            _activeUpstreamFingerprint = "";
            SetRunningState(false);
        }
        catch (Exception exception)
        {
            AppendLog("停止时出现错误：" + exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RefreshIpButton_Click(object sender, RoutedEventArgs e) => await RefreshIpAsync();

    private async Task RefreshIpAsync()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        RefreshIpButton.IsEnabled = false;
        NormalIpText.Text = "检测中…";
        StaticIpText.Text = _core.IsRunning ? "检测中…" : "开启后检测";
        AppendLog("正在并行检测普通出口和静态出口…");
        try
        {
            var normalProxy = _activeNormalProxy ?? GetProxyForRefresh();
            var normalTask = GetIpAsync(normalProxy);
            var staticTask = _core.IsRunning
                ? GetIpAsync(new WebProxy($"http://127.0.0.1:{_core.StaticCheckPort}"))
                : Task.FromResult("开启后检测");
            await Task.WhenAll(normalTask, staticTask);
            NormalIpText.Text = await normalTask;
            StaticIpText.Text = await staticTask;
            AppendLog($"出口检测完成：普通 {NormalIpText.Text}；静态 {StaticIpText.Text}");
            if (_boundPhysicalAdapter && StaticIpText.Text.StartsWith("检测失败", StringComparison.Ordinal))
                AppendLog("提示：当前绑定的是物理网卡。静态出口需经 VPN 中转，请确认 VPN 已连接后点击“刷新”。");
        }
        catch (Exception exception)
        {
            NormalIpText.Text = "检测失败：" + exception.Message;
            AppendLog("出口检测失败：" + exception.Message);
        }
        finally
        {
            _refreshing = false;
            RefreshIpButton.IsEnabled = true;
        }
    }

    private static async Task<string> GetIpAsync(IWebProxy? proxy)
    {
        string? lastError = null;
        using var handler = new HttpClientHandler { UseProxy = proxy is not null, Proxy = proxy };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var endpoint in new[]
        {
            "https://api.ipify.org", "https://checkip.amazonaws.com", "https://icanhazip.com"
        })
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var value = (await client.GetStringAsync(endpoint, timeout.Token)).Trim();
                if (IPAddress.TryParse(value, out _))
                    return value;
            }
            catch (Exception exception)
            {
                lastError = exception is OperationCanceledException ? "连接超时" : exception.Message;
            }
        }
        return "检测失败：" + (lastError ?? "检测服务无有效响应");
    }

    private AppSettings ReadSettings(bool validateForStart = true)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port))
            throw new InvalidOperationException("端口必须是数字。");

        var typeItem = (ComboBoxItem)ProxyTypeBox.SelectedItem;
        var upstreamItem = (ComboBoxItem)UpstreamModeBox.SelectedItem;
        var upstreamMode = upstreamItem.Tag?.ToString() ?? "auto";
        var settings = new AppSettings
        {
            ProxyType = typeItem.Tag?.ToString() ?? "socks",
            Server = ServerBox.Text.Trim(),
            Port = port,
            Username = UsernameBox.Text,
            Password = PasswordBox.Password,
            UpstreamMode = upstreamMode,
            Domains = DomainsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList()
        };
        if (upstreamMode.StartsWith("manual-", StringComparison.Ordinal))
        {
            var address = ParseAddress(UpstreamAddressBox.Text);
            settings.UpstreamType = upstreamMode == "manual-socks" ? "socks" : "http";
            settings.UpstreamHost = address.Host;
            settings.UpstreamPort = address.Port;
        }
        settings.Domains = SingBoxConfigBuilder.NormalizeDomains(settings.Domains);
        if (validateForStart)
            _ = SingBoxConfigBuilder.Build(settings, 20808);
        return settings;
    }

    private void LoadSettings(AppSettings settings)
    {
        ProxyTypeBox.SelectedIndex = settings.ProxyType == "http" ? 1 : 0;
        UpstreamModeBox.SelectedItem = UpstreamModeBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == settings.UpstreamMode) ?? (ComboBoxItem)UpstreamModeBox.Items[0];
        UpstreamAddressBox.Text = settings.UpstreamPort > 0 ? $"{settings.UpstreamHost}:{settings.UpstreamPort}" : "";
        UpdateUpstreamAddressState();
        ServerBox.Text = settings.Server;
        PortBox.Text = settings.Port.ToString();
        UsernameBox.Text = settings.Username;
        PasswordBox.Password = settings.Password;
        DomainsBox.Text = string.Join(Environment.NewLine, settings.Domains);
    }

    private void SetRunningState(bool running)
    {
        if (running)
            _upstreamWatchdog.Start();
        else
            _upstreamWatchdog.Stop();
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ProxyTypeBox.IsEnabled = !running;
        ServerBox.IsEnabled = !running;
        PortBox.IsEnabled = !running;
        UsernameBox.IsEnabled = !running;
        PasswordBox.IsEnabled = !running;
        DomainsBox.IsEnabled = !running;
        UpstreamModeBox.IsEnabled = !running;
        UpstreamAddressBox.IsEnabled = !running && IsManualUpstreamMode();
        StatusText.Text = running ? "分流运行中" : "已关闭";
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#20A464" : "#9AA3B2"));
        if (!running)
            StaticIpText.Text = "开启后检测";
    }

    private void SetBusy(bool busy)
    {
        if (busy)
        {
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
        }
        else
        {
            StartButton.IsEnabled = !_core.IsRunning;
            StopButton.IsEnabled = _core.IsRunning;
        }
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        _logLineCount++;
        if (_logLineCount > 800)
        {
            LogBox.Text = string.Join(Environment.NewLine, LogBox.Text.Split(Environment.NewLine).TakeLast(500));
            _logLineCount = 500;
        }
        LogBox.ScrollToEnd();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _core.Dispose();
            return;
        }
        e.Cancel = true;
        NetworkChange.NetworkAddressChanged -= NetworkAddressChanged;
        _networkChangeDebounce?.Cancel();
        if (_restoring)
            return;
        _restoring = true;
        StatusText.Text = "正在恢复网络，请稍候…";
        SetBusy(true);
        try
        {
            SettingsStore.Save(ReadSettings(validateForStart: false));
        }
        catch (Exception exception)
        {
            AppendLog("退出时未保存不完整配置：" + exception.Message);
        }
        try
        {
            await _core.StopAsync();
        }
        catch (Exception exception)
        {
            AppendLog("退出恢复出现错误：" + exception.Message);
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void UpstreamModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_core.IsRunning)
            _activeNormalProxy = null;
        UpdateUpstreamAddressState();
    }

    private void UpdateUpstreamAddressState()
    {
        if (UpstreamAddressBox is not null)
            UpstreamAddressBox.IsEnabled = !_core.IsRunning && IsManualUpstreamMode();
    }

    private bool IsManualUpstreamMode() => UpstreamModeBox.SelectedItem is ComboBoxItem item &&
        (item.Tag?.ToString()?.StartsWith("manual-", StringComparison.Ordinal) ?? false);

    private void ConfigureUpstream(AppSettings settings, bool requireSystemProxy)
    {
        settings.UpstreamInterfaceName = "";
        settings.UpstreamInterfaceIndex = 0;
        _boundPhysicalAdapter = false;
        if (settings.UpstreamMode is "auto" or "system")
        {
            var detected = SingBoxConfigBuilder.DetectWindowsSystemProxy();
            if (detected is null && _core.IsRunning && _core.BackupProxyEnabled)
                detected = SingBoxConfigBuilder.DetectProxyFromValue(_core.BackupProxyServer);
            if (detected is null)
            {
                if (requireSystemProxy)
                    throw new InvalidOperationException("Windows 系统代理未开启或地址无效。");
                ConfigureInterfaceUpstream(settings, required: false);
                return;
            }
            settings.UpstreamType = detected.Value.Type;
            settings.UpstreamHost = detected.Value.Host;
            settings.UpstreamPort = detected.Value.Port;
        }
        else if (settings.UpstreamMode == "adapter")
        {
            ConfigureInterfaceUpstream(settings, required: true);
            return;
        }

        settings.UpstreamProcessName = IPAddress.TryParse(settings.UpstreamHost, out var address) && IPAddress.IsLoopback(address)
            ? SingBoxConfigBuilder.FindListeningProcessName(settings.UpstreamPort)
            : "";
        var scheme = settings.UpstreamType == "socks" ? "socks5" : "http";
        _activeNormalProxy = new WebProxy($"{scheme}://{settings.UpstreamHost}:{settings.UpstreamPort}");
        AppendLog($"上游：{settings.UpstreamType.ToUpperInvariant()} {settings.UpstreamHost}:{settings.UpstreamPort}" +
                  (string.IsNullOrEmpty(settings.UpstreamProcessName) ? "" : $"；防环路进程 {settings.UpstreamProcessName}"));
    }

    private void ConfigureInterfaceUpstream(AppSettings settings, bool required)
    {
        settings.UpstreamHost = "";
        settings.UpstreamPort = 0;
        settings.UpstreamProcessName = "";
        _activeNormalProxy = null;
        var network = SingBoxConfigBuilder.DetectDefaultInterface();
        if (network is null)
        {
            if (required)
                throw new InvalidOperationException("没有检测到可用的默认 VPN 网卡。请先连接 VPN，或改用手动本地代理模式。");
            AppendLog("无法确定默认 VPN 网卡，将回退到 sing-box 自动接口选择。");
            return;
        }
        settings.UpstreamInterfaceName = network.Value.Name;
        settings.UpstreamInterfaceIndex = network.Value.Index;
        if (LooksLikePhysicalAdapter(network.Value.Description))
        {
            _boundPhysicalAdapter = true;
            AppendLog($"未检测到 VPN 虚拟网卡，已绑定默认接口 {network.Value.Name}（{network.Value.Address}）。" +
                      "静态链路需要 VPN 在线；若静态出口失败，请先连接 VPN 后重新开启分流。");
        }
        else
        {
            AppendLog($"检测到当前 VPN 网卡：{network.Value.Name}（{network.Value.Address}），已绑定当前接口。");
        }
    }

    private static bool LooksLikePhysicalAdapter(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;
        var keywords = new[] { "Wi-Fi", "WiFi", "Wireless", "802.11", "Ethernet", "以太网", "Realtek", "MediaTek", "Killer", "Broadcom", "Qualcomm", "Marvell" };
        return keywords.Any(keyword => description.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private IWebProxy? GetProxyForRefresh()
    {
        var mode = (UpstreamModeBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        if (mode == "adapter")
            return null;
        if (mode.StartsWith("manual-", StringComparison.Ordinal))
        {
            var address = ParseAddress(UpstreamAddressBox.Text);
            var scheme = mode == "manual-socks" ? "socks5" : "http";
            return new WebProxy($"{scheme}://{address.Host}:{address.Port}");
        }
        var detected = SingBoxConfigBuilder.DetectWindowsSystemProxy();
        if (detected is null)
            return null;
        var detectedScheme = detected.Value.Type == "socks" ? "socks5" : "http";
        return new WebProxy($"{detectedScheme}://{detected.Value.Host}:{detected.Value.Port}");
    }

    private static (string Host, int Port) ParseAddress(string value)
    {
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host) || uri.Port <= 0)
            throw new InvalidOperationException("上游地址格式无效，请填写例如 127.0.0.1:7897。");
        return (uri.Host, uri.Port);
    }

    private void NetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!_core.IsRunning || _restoring || _restartingForNetworkChange)
            return;
        _networkChangeDebounce?.Cancel();
        _networkChangeDebounce?.Dispose();
        _networkChangeDebounce = new CancellationTokenSource();
        var token = _networkChangeDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
                await Dispatcher.InvokeAsync(() => RebuildForNetworkChangeAsync());
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private async Task RebuildForNetworkChangeAsync()
    {
        if (!_core.IsRunning || _restoring || _restartingForNetworkChange)
            return;

        AppSettings settings;
        var backupProxyWasEnabled = _core.BackupProxyEnabled;
        try
        {
            settings = ReadSettings();
            ConfigureUpstream(settings, requireSystemProxy: settings.UpstreamMode == "system");
        }
        catch (Exception exception)
        {
            AppendLog("网络变化检测失败，将保留当前分流：" + exception.Message);
            return;
        }

        var fingerprint = GetUpstreamFingerprint(settings);
        if (fingerprint == _activeUpstreamFingerprint)
            return;

        _restartingForNetworkChange = true;
        SetBusy(true);
        AppendLog("检测到 VPN/上游网络变化，正在自动重建分流…");
        try
        {
            var preserveDisabledProxyState = settings.UpstreamMode == "auto" && backupProxyWasEnabled &&
                string.IsNullOrWhiteSpace(settings.UpstreamHost);
            await _core.StopAsync(preserveDisabledProxyState);
            await _core.StartAsync(settings);
            _activeUpstreamFingerprint = GetUpstreamFingerprint(settings);
            _lastCheapSignal = GetCheapNetworkSignal();
            SetRunningState(true);
            AppendLog("分流已跟随新的 VPN/上游网络恢复。");
            await RefreshIpAsync();
        }
        catch (Exception exception)
        {
            AppendLog("自动重建失败，分流已关闭：" + exception.Message);
            try { await _core.StopAsync(); } catch { }
            _activeUpstreamFingerprint = "";
            SetRunningState(false);
        }
        finally
        {
            _restartingForNetworkChange = false;
            SetBusy(false);
        }
    }

    private string GetUpstreamFingerprint(AppSettings settings) =>
        $"{settings.UpstreamMode}|{settings.UpstreamType}|{settings.UpstreamHost}|{settings.UpstreamPort}|" +
        $"{settings.UpstreamInterfaceName}|{settings.UpstreamInterfaceIndex}|{IsPacIntact()}";

    private bool IsPacIntact()
    {
        var pacUrl = _core.PacUrl;
        if (string.IsNullOrEmpty(pacUrl))
            return false;
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        return string.Equals(key?.GetValue("AutoConfigURL") as string, pacUrl, StringComparison.Ordinal);
    }
}
