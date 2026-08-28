using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace RouteProxy;

public sealed class SingBoxProcess : IDisposable
{
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteProxy");
    private Process? _process;
    private JobObject? _job;
    private PacProxyController? _pacProxy;
    private string? _runtimeConfigPath;
    private bool _stopping;

    public event Action<string>? LogLine;
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };
    public int StaticCheckPort { get; private set; }
    public string PacUrl => _pacProxy?.ActivePacUrl ?? "";
    public bool BackupProxyEnabled => _pacProxy?.BackupProxyEnabled ?? false;
    public string BackupProxyServer => _pacProxy?.BackupProxyServer ?? "";
    private string CorePath => Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");

    public async Task StartAsync(AppSettings settings)
    {
        if (IsRunning)
            return;
        if (!File.Exists(CorePath))
            throw new FileNotFoundException("找不到 sing-box 核心。请重新解压完整程序目录。", CorePath);

        Directory.CreateDirectory(_dataDirectory);
        StaticCheckPort = FindFreePort();
        _runtimeConfigPath = Path.Combine(_dataDirectory, $"runtime-{Environment.ProcessId}.json");
        var config = SingBoxConfigBuilder.Build(settings, StaticCheckPort);
        await File.WriteAllTextAsync(_runtimeConfigPath, config, new UTF8Encoding(false));

        try
        {
            var checkResult = await RunCheckAsync(_runtimeConfigPath);
            if (checkResult.ExitCode != 0)
                throw new InvalidOperationException("sing-box 配置检查失败：\n" + Redact(checkResult.Output, settings));

            _stopping = false;
            _process = CreateProcess("run", "-c", _runtimeConfigPath);
            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += (_, e) => WriteLog(e.Data);
            _process.ErrorDataReceived += (_, e) => WriteLog(e.Data);
            _process.Exited += ProcessExited;
            _job = new JobObject();

            if (!_process.Start())
                throw new InvalidOperationException("无法启动 sing-box。");
            _job.Assign(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            WriteLog("sing-box 已启动，正在建立本地静态代理入口。");
            await Task.Delay(300);
            if (_process.HasExited)
                throw new InvalidOperationException("sing-box 启动后立即退出，请查看日志。");
            _pacProxy = new PacProxyController(WriteLog);
            await _pacProxy.StartAsync(SingBoxConfigBuilder.NormalizeDomains(settings.Domains), StaticCheckPort);
            WriteLog("按域名 PAC 分流已就绪；普通流量不会进入 sing-box。");
            DeleteRuntimeConfig();
            WriteLog("运行时配置已从磁盘删除。");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task StopAsync(bool preserveDisabledProxyState = false)
    {
        _stopping = true;
        if (_pacProxy is not null)
        {
            await _pacProxy.StopAsync(preserveDisabledProxyState);
            _pacProxy = null;
        }
        var process = _process;
        if (process is { HasExited: false })
        {
            WriteLog("正在停止分流并恢复网络…");
            process.Kill(entireProcessTree: true);
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                WriteLog("核心未及时退出，正在执行强制清理…");
                _job?.Dispose();
                _job = null;
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (TimeoutException)
                {
                    // Continue recovery even if Windows has not reaped the process handle yet.
                }
            }
        }
        CleanupProcess();
        DeleteRuntimeConfig();
        WriteLog("网络恢复完成，可以安全退出。");
    }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Closing the Job Object below is the final kill-on-close fallback.
        }
        CleanupProcess();
        DeleteRuntimeConfig();
    }

    private Process CreateProcess(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(CorePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return new Process { StartInfo = startInfo };
    }

    private async Task<(int ExitCode, string Output)> RunCheckAsync(string configPath)
    {
        using var process = CreateProcess("check", "-c", configPath);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, (await outputTask) + (await errorTask));
    }

    private void ProcessExited(object? sender, EventArgs e)
    {
        if (!_stopping)
        {
            WriteLog("sing-box 意外退出；看门狗将恢复启动前的 Windows 代理设置。");
            Exited?.Invoke();
        }
    }

    private void CleanupProcess()
    {
        if (_process is not null)
        {
            _process.Exited -= ProcessExited;
            _process.Dispose();
            _process = null;
        }
        _job?.Dispose();
        _job = null;
    }

    private void DeleteRuntimeConfig()
    {
        if (_runtimeConfigPath is null)
            return;
        try
        {
            File.Delete(_runtimeConfigPath);
        }
        catch
        {
            // Best effort; the config lives only in the current user's local data directory.
        }
        _runtimeConfigPath = null;
    }

    private void WriteLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        line = AnsiEscape.Replace(line, "");
        if (
            line.Contains("router: found process path:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("inbound packet connection from", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("inbound connection from", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("inbound connection to", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("outbound connection to", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("connection upload closed: raw-read tcp 127.0.0.1:", StringComparison.OrdinalIgnoreCase))
            return;
        LogLine?.Invoke(line);
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Redact(string output, AppSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.Password))
            output = output.Replace(settings.Password, "******", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(settings.Username))
            output = output.Replace(settings.Username, "******", StringComparison.Ordinal);
        return output.Trim();
    }
}
