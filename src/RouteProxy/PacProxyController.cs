using Microsoft.Win32;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RouteProxy;

internal sealed class PacProxyController : IAsyncDisposable
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RouteProxy", "system-proxy-backup.json");
    private readonly Action<string> _log;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _listenLoop;
    private ProxyBackup? _backup;
    private byte[] _response = [];
    private string _activePacUrl = "";

    public PacProxyController(Action<string> log) => _log = log;

    public string ActivePacUrl => _activePacUrl;

    public bool BackupProxyEnabled => _backup is { ProxyEnableExists: true, ProxyEnable: 1 };
    public string BackupProxyServer => _backup?.ProxyServer ?? "";

    public async Task StartAsync(IReadOnlyCollection<string> domains, int staticProxyPort)
    {
        RestoreSavedBackup();
        _backup = CaptureBackup();
        if (_backup.AutoConfigUrlExists && !string.IsNullOrWhiteSpace(_backup.AutoConfigUrl))
            throw new InvalidOperationException("当前 Windows 已使用其他 PAC 自动代理脚本，RouteProxy 不会覆盖它。请先关闭该 PAC，或改用 VPN 网卡模式。");

        _cancellation = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var pacPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var pac = BuildPac(domains, staticProxyPort, GetDefaultDirective(_backup));
        var body = Encoding.UTF8.GetBytes(pac);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: application/x-ns-proxy-autoconfig\r\n" +
            $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        _response = [.. header, .. body];
        _listenLoop = RunListenerAsync(_cancellation.Token);

        try
        {
            SaveBackup(_backup);
            var pacUrl = $"http://127.0.0.1:{pacPort}/routeproxy.pac?v={Guid.NewGuid():N}";
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
                ?? throw new InvalidOperationException("无法打开 Windows 当前用户代理设置。");
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
            key.SetValue("AutoConfigURL", pacUrl, RegistryValueKind.String);
            NotifyProxyChanged();
            _activePacUrl = pacUrl;
            _log($"PAC 域名分流已启用：{domains.Count} 个域名后缀；普通网站保持当前 VPN。 ");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync(bool preserveDisabledProxyState = false)
    {
        if (_backup is not null)
        {
            PreserveExternalChanges(preserveDisabledProxyState);
            RestoreBackup(_backup);
            _backup = null;
            TryDeleteBackup();
            _log("Windows 代理设置已恢复为启动前状态。");
        }
        _activePacUrl = "";

        _cancellation?.Cancel();
        _listener?.Stop();
        if (_listenLoop is not null)
        {
            try { await _listenLoop; }
            catch { }
        }
        _listener = null;
        _listenLoop = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task RunListenerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);
                _ = ServePacAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception exception) { _log("PAC 本地服务错误：" + exception.Message); }
        }
    }

    private async Task ServePacAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = new byte[2048];
                _ = await stream.ReadAsync(request, cancellationToken);
                await stream.WriteAsync(_response, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) { }
        }
    }

    private static string BuildPac(IReadOnlyCollection<string> domains, int proxyPort, string defaultDirective)
    {
        var matches = string.Join(" ||\n        ", domains.Select(domain =>
            $"host === \"{domain}\" || dnsDomainIs(host, \".{domain}\")"));
        return "function FindProxyForURL(url, host) {\n" +
               "    host = host.toLowerCase();\n" +
               $"    if ({matches})\n" +
               $"        return \"PROXY 127.0.0.1:{proxyPort}\";\n" +
               $"    return \"{defaultDirective}\";\n" +
               "}\n";
    }

    private static string GetDefaultDirective(ProxyBackup backup)
    {
        if (!backup.ProxyEnableExists || backup.ProxyEnable != 1 || string.IsNullOrWhiteSpace(backup.ProxyServer))
            return "DIRECT";

        var entries = backup.ProxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Split('=', 2)).ToArray();
        var selected = entries.FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("https", StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("http", StringComparison.OrdinalIgnoreCase))
            ?? entries.FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("socks", StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
            return (selected[0].Equals("socks", StringComparison.OrdinalIgnoreCase) ? "SOCKS5 " : "PROXY ") + selected[1].Trim();
        return "PROXY " + backup.ProxyServer.Trim();
    }

    private static ProxyBackup CaptureBackup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath)
            ?? throw new InvalidOperationException("无法读取 Windows 当前用户代理设置。");
        var names = key.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ProxyBackup
        {
            ProxyEnableExists = names.Contains("ProxyEnable"),
            ProxyEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0)),
            ProxyServerExists = names.Contains("ProxyServer"),
            ProxyServer = key.GetValue("ProxyServer") as string ?? "",
            AutoConfigUrlExists = names.Contains("AutoConfigURL"),
            AutoConfigUrl = key.GetValue("AutoConfigURL") as string ?? "",
            AutoDetectExists = names.Contains("AutoDetect"),
            AutoDetect = Convert.ToInt32(key.GetValue("AutoDetect", 0))
        };
    }

    private static void SaveBackup(ProxyBackup backup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup), new UTF8Encoding(false));
    }

    private static void RestoreSavedBackup()
    {
        if (!File.Exists(BackupPath))
            return;
        try
        {
            var backup = JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(BackupPath));
            if (backup is not null)
                RestoreBackup(backup);
            TryDeleteBackup();
        }
        catch
        {
            throw new InvalidOperationException("发现上次异常退出留下的代理备份，但自动恢复失败。请运行 Recover-Network.cmd。");
        }
    }

    private static void RestoreBackup(ProxyBackup backup)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法恢复 Windows 当前用户代理设置。");
        RestoreValue(key, "ProxyEnable", backup.ProxyEnableExists, backup.ProxyEnable, RegistryValueKind.DWord);
        RestoreValue(key, "ProxyServer", backup.ProxyServerExists, backup.ProxyServer, RegistryValueKind.String);
        RestoreValue(key, "AutoConfigURL", backup.AutoConfigUrlExists, backup.AutoConfigUrl, RegistryValueKind.String);
        RestoreValue(key, "AutoDetect", backup.AutoDetectExists, backup.AutoDetect, RegistryValueKind.DWord);
        NotifyProxyChanged();
    }

    private static void RestoreValue(RegistryKey key, string name, bool existed, object value, RegistryValueKind kind)
    {
        if (existed)
            key.SetValue(name, value, kind);
        else
            key.DeleteValue(name, throwOnMissingValue: false);
    }

    private void PreserveExternalChanges(bool preserveDisabledProxyState)
    {
        if (_backup is null)
            return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key is null)
                return;
            var names = key.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentEnable = Convert.ToInt32(key.GetValue("ProxyEnable", 0));
            var currentServer = key.GetValue("ProxyServer") as string ?? "";
            var currentAutoConfig = key.GetValue("AutoConfigURL") as string ?? "";
            var currentAutoDetect = Convert.ToInt32(key.GetValue("AutoDetect", 0));

            // While our PAC is intact, ProxyEnable=0 and AutoDetect=0 are RouteProxy's
            // own values, so the original snapshot must be restored. When another
            // client removes the PAC, only adopt the disabled state if the caller has
            // independently established that the upstream moved away from its proxy.
            if (string.Equals(currentAutoConfig, _activePacUrl, StringComparison.Ordinal))
                return;

            var serverChanged = !string.Equals(currentServer, _backup.ProxyServer, StringComparison.Ordinal);
            var autoDetectChanged = currentAutoDetect != 0;
            if (currentEnable != 0 || serverChanged || autoDetectChanged || preserveDisabledProxyState)
            {
                _backup.ProxyEnableExists = names.Contains("ProxyEnable");
                _backup.ProxyEnable = currentEnable;
            }
            if (serverChanged)
            {
                _backup.ProxyServerExists = names.Contains("ProxyServer");
                _backup.ProxyServer = currentServer;
            }
            _backup.AutoConfigUrlExists = names.Contains("AutoConfigURL");
            _backup.AutoConfigUrl = currentAutoConfig;
            if (autoDetectChanged || preserveDisabledProxyState)
            {
                _backup.AutoDetectExists = names.Contains("AutoDetect");
                _backup.AutoDetect = currentAutoDetect;
            }
        }
        catch
        {
            // Baseline adoption is best effort; the original backup remains the fallback.
        }
    }

    private static void TryDeleteBackup()
    {
        try { File.Delete(BackupPath); } catch { }
    }

    private static void NotifyProxyChanged()
    {
        _ = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int bufferLength);

    private sealed class ProxyBackup
    {
        public bool ProxyEnableExists { get; set; }
        public int ProxyEnable { get; set; }
        public bool ProxyServerExists { get; set; }
        public string ProxyServer { get; set; } = "";
        public bool AutoConfigUrlExists { get; set; }
        public string AutoConfigUrl { get; set; } = "";
        public bool AutoDetectExists { get; set; }
        public int AutoDetect { get; set; }
    }
}
