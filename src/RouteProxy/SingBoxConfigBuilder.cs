using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace RouteProxy;

public static class SingBoxConfigBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Build(AppSettings settings, int checkPort)
    {
        Validate(settings);
        var domains = NormalizeDomains(settings.Domains);
        if (domains.Count == 0)
            throw new InvalidOperationException("至少需要填写一个有效域名。");

        var proxy = new Dictionary<string, object?>
        {
            ["type"] = settings.ProxyType,
            ["tag"] = "static-proxy",
            ["server"] = settings.Server.Trim(),
            ["server_port"] = settings.Port
        };
        if (settings.ProxyType == "socks")
        {
            proxy["version"] = "5";
            proxy["network"] = "tcp";
        }
        if (!string.IsNullOrWhiteSpace(settings.Username))
            proxy["username"] = settings.Username;
        if (!string.IsNullOrEmpty(settings.Password))
            proxy["password"] = settings.Password;

        var hasUpstream = !string.IsNullOrWhiteSpace(settings.UpstreamHost) && settings.UpstreamPort > 0;
        if (hasUpstream)
            proxy["detour"] = "upstream-vpn";

        var dnsServer = new Dictionary<string, object?>
        {
            ["type"] = "https",
            ["tag"] = "secure-dns",
            ["server"] = "1.1.1.1",
            ["server_port"] = 443,
            ["tls"] = new { enabled = true, server_name = "cloudflare-dns.com" }
        };
        if (hasUpstream)
            dnsServer["detour"] = "upstream-vpn";

        var outbounds = new List<object>();
        if (hasUpstream)
        {
            var upstream = new Dictionary<string, object?>
            {
                ["type"] = settings.UpstreamType,
                ["tag"] = "upstream-vpn",
                ["server"] = settings.UpstreamHost,
                ["server_port"] = settings.UpstreamPort
            };
            if (settings.UpstreamType == "socks")
            {
                upstream["version"] = "5";
                upstream["network"] = "tcp";
            }
            outbounds.Add(upstream);
        }
        outbounds.Add(proxy);
        outbounds.Add(new Dictionary<string, object?> { ["type"] = "direct", ["tag"] = "direct" });

        var rules = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["inbound"] = "static-check",
                ["action"] = "route",
                ["outbound"] = "static-proxy"
            },
        };

        var route = new Dictionary<string, object?>
        {
            ["default_domain_resolver"] = "secure-dns",
            ["rules"] = rules,
            ["final"] = hasUpstream ? "upstream-vpn" : "direct"
        };
        if (!string.IsNullOrWhiteSpace(settings.UpstreamInterfaceName))
            route["default_interface"] = settings.UpstreamInterfaceName;
        else
            route["auto_detect_interface"] = true;

        var config = new Dictionary<string, object?>
        {
            ["log"] = new { level = "info", timestamp = true },
            ["dns"] = new Dictionary<string, object?>
            {
                ["servers"] = new object[] { dnsServer },
                ["final"] = "secure-dns",
                ["strategy"] = "ipv4_only",
                ["reverse_mapping"] = true
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "mixed",
                    ["tag"] = "static-check",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = checkPort
                }
            },
            ["outbounds"] = outbounds,
            ["route"] = route
        };

        return JsonSerializer.Serialize(config, JsonOptions);
    }

    public static (string Type, string Host, int Port)? DetectWindowsSystemProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key?.GetValue("ProxyEnable") is not int enabled || enabled != 1)
            return null;
        return DetectProxyFromValue(key.GetValue("ProxyServer") as string);
    }

    public static (string Type, string Host, int Port)? DetectProxyFromValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var type = "http";
        if (value.Contains(';'))
        {
            var selected = value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .OrderBy(parts => parts[0].Equals("socks", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (selected is null)
                return null;
            type = selected[0].Equals("socks", StringComparison.OrdinalIgnoreCase) ? "socks" : "http";
            value = selected[1];
        }
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var address = value.Trim();
        if (address.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
            type = "socks";
        if (!address.Contains("://", StringComparison.Ordinal))
            address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Port <= 0)
            return null;
        return CanUseProxy(type, uri.Host, uri.Port) ? (type, uri.Host, uri.Port) : null;
    }

    public static string FindListeningProcessName(int port)
    {
        try
        {
            var startInfo = new ProcessStartInfo("netstat.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("-ano");
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add("tcp");
            using var netstat = Process.Start(startInfo);
            if (netstat is null)
                return "";
            var output = netstat.StandardOutput.ReadToEnd();
            netstat.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) ||
                    !parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase) ||
                    !parts[1].EndsWith(":" + port, StringComparison.Ordinal))
                    continue;
                if (!int.TryParse(parts[^1], out var processId))
                    continue;
                using var process = Process.GetProcessById(processId);
                return process.ProcessName + ".exe";
            }
        }
        catch
        {
            // Process matching is an optimization; startup validation will still catch loops.
        }
        return "";
    }

    private static bool CanUseProxy(string type, string host, int port)
    {
        try
        {
            var scheme = type == "socks" ? "socks5" : "http";
            using var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = new WebProxy($"{scheme}://{host}:{port}")
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
            var value = client.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();
            return IPAddress.TryParse(value, out _);
        }
        catch
        {
            return false;
        }
    }

    public static (string Name, string Description, string Address, int Index)? DetectDefaultInterface()
    {
        try
        {
            var startInfo = new ProcessStartInfo("route.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            startInfo.ArgumentList.Add("print");
            startInfo.ArgumentList.Add("-4");
            using var route = Process.Start(startInfo);
            if (route is null)
                return null;
            var output = route.StandardOutput.ReadToEnd();
            route.WaitForExit(3000);

            var candidates = new List<(string Address, int Metric)>();
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5 || parts[0] != "0.0.0.0" || parts[1] != "0.0.0.0" ||
                    !IPAddress.TryParse(parts[3], out _) || !int.TryParse(parts[4], out var metric))
                    continue;
                candidates.Add((parts[3], metric));
            }

            foreach (var candidate in candidates.OrderBy(item => item.Metric))
            {
                var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(adapter =>
                    adapter.OperationalStatus == OperationalStatus.Up &&
                    !adapter.Name.Equals("RouteProxy", StringComparison.OrdinalIgnoreCase) &&
                    adapter.GetIPProperties().UnicastAddresses.Any(unicast =>
                        unicast.Address.AddressFamily == AddressFamily.InterNetwork &&
                        unicast.Address.ToString() == candidate.Address));
                if (network is not null)
                    return (network.Name, network.Description, candidate.Address,
                        network.GetIPProperties().GetIPv4Properties()?.Index ?? 0);
            }
        }
        catch
        {
            // The caller can fall back to sing-box interface auto-detection.
        }
        return null;
    }

    public static List<string> NormalizeDomains(IEnumerable<string> domains)
    {
        var idn = new IdnMapping();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in domains)
        {
            var domain = source.Trim().TrimEnd('.');
            if (domain.StartsWith("*.", StringComparison.Ordinal))
                domain = domain[2..];
            domain = domain.TrimStart('.');
            if (domain.Length == 0)
                continue;
            if (domain.Contains("://", StringComparison.Ordinal) || domain.Contains('/') || domain.Contains(' '))
                throw new InvalidOperationException($"域名格式无效：{source}");
            try
            {
                domain = idn.GetAscii(domain).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                throw new InvalidOperationException($"域名格式无效：{source}");
            }
            result.Add(domain);
        }
        return result.Order(StringComparer.Ordinal).ToList();
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.ProxyType is not ("socks" or "http"))
            throw new InvalidOperationException("代理类型只能是 SOCKS5 或 HTTP。");
        if (string.IsNullOrWhiteSpace(settings.Server))
            throw new InvalidOperationException("请填写静态代理服务器地址。");
        if (settings.Port is < 1 or > 65535)
            throw new InvalidOperationException("代理端口必须在 1 到 65535 之间。");
    }
}
