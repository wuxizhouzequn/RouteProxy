using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace RouteProxy;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, @"Global\RouteProxy.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("RouteProxy 已经在运行。请先关闭原窗口，再启动新版本。", "RouteProxy",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        StartRecoveryWatchdog();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void StartRecoveryWatchdog()
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Recover-Network.ps1");
        if (!File.Exists(scriptPath))
            return;
        try
        {
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-WaitForProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add("-Silent");
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            // Normal shutdown still performs synchronous cleanup; the bundled
            // recovery script remains available as a manual fallback.
        }
    }
}
