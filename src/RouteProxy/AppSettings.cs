using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace RouteProxy;

public sealed class AppSettings
{
    public string ProxyType { get; set; } = "socks";
    public string Server { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string UpstreamMode { get; set; } = "auto";
    public string UpstreamType { get; set; } = "http";
    public string UpstreamHost { get; set; } = "";
    public int UpstreamPort { get; set; }
    public string UpstreamProcessName { get; set; } = "";
    public string UpstreamInterfaceName { get; set; } = "";
    public int UpstreamInterfaceIndex { get; set; }
    public List<string> Domains { get; set; } =
    [
        "chatgpt.com",
        "openai.com",
        "oaistatic.com",
        "oaiusercontent.com",
        "google.com",
        "googleapis.com",
        "gstatic.com",
        "googleusercontent.com",
        "ggpht.com",
        "youtube.com",
        "youtu.be",
        "ytimg.com",
        "googlevideo.com",
        "claude.ai",
        "anthropic.com",
        "notebooklm.google.com",
        "ping0.cc"
    ];
}

public static class SettingsStore
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RouteProxy");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return new AppSettings();

        try
        {
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath));
            if (stored is null)
                return new AppSettings();

            var domains = stored.Domains.Count > 0 ? stored.Domains : new AppSettings().Domains;
            if (stored.SettingsVersion < 2)
                domains = domains.Concat(new AppSettings().Domains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            return new AppSettings
            {
                ProxyType = stored.ProxyType,
                Server = stored.Server,
                Port = stored.Port,
                Username = stored.Username,
                Password = Unprotect(stored.ProtectedPassword),
                UpstreamMode = stored.UpstreamMode,
                UpstreamType = stored.UpstreamType,
                UpstreamHost = stored.UpstreamHost,
                UpstreamPort = stored.UpstreamPort,
                Domains = domains
            };
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var stored = new StoredSettings
        {
            SettingsVersion = 2,
            ProxyType = settings.ProxyType,
            Server = settings.Server,
            Port = settings.Port,
            Username = settings.Username,
            ProtectedPassword = Protect(settings.Password),
            UpstreamMode = settings.UpstreamMode,
            UpstreamType = settings.UpstreamType,
            UpstreamHost = settings.UpstreamHost,
            UpstreamPort = settings.UpstreamPort,
            Domains = settings.Domains
        };
        var temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(stored, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, FilePath, true);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }

    private static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
    }

    private sealed class StoredSettings
    {
        public int SettingsVersion { get; set; }
        public string ProxyType { get; set; } = "socks";
        public string Server { get; set; } = "";
        public int Port { get; set; } = 443;
        public string Username { get; set; } = "";
        public string ProtectedPassword { get; set; } = "";
        public string UpstreamMode { get; set; } = "auto";
        public string UpstreamType { get; set; } = "http";
        public string UpstreamHost { get; set; } = "";
        public int UpstreamPort { get; set; }
        public List<string> Domains { get; set; } = [];
    }
}
