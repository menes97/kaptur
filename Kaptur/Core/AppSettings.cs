using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Kaptur;

static class AppPaths
{
    /// KAPTUR_HOME ile değiştirilebilir (taşınabilir kullanım ya da test için).
    public static string Root { get; } =
        Environment.GetEnvironmentVariable("KAPTUR_HOME") is { Length: > 0 } home
            ? Path.GetFullPath(home)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kaptur");
    public static string Tools => Path.Combine(Root, "tools");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "kaptur.log");
}

enum CookieSource { None, Firefox, File }

sealed class AppSettings
{
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kaptur");

    /// Instagram neredeyse her zaman giriş ister; X'in herkese açık videoları çerezsiz iner.
    public CookieSource Cookies { get; set; } = CookieSource.None;
    public string CookiesFile { get; set; } = "";

    public bool TranscribeByDefault { get; set; }
    public string WhisperModel { get; set; } = "large-v3-turbo";
    public string WhisperLanguage { get; set; } = "auto";
    public string WhisperDevice { get; set; } = "auto";   // auto | cuda | cpu

    public bool WatchClipboard { get; set; } = true;
    public bool AutoDownloadFromClipboard { get; set; }
    public bool StartWithWindows { get; set; }
    public int MaxParallelDownloads { get; set; } = 2;

    /// Chrome eklentisinin bağlandığı yerel köprü. mc-download-manager 48213'ü kullanır; çakışmasın diye 48214.
    public bool BridgeEnabled { get; set; } = true;
    public int BridgePort { get; set; } = 48214;
    /// Web Store'dan ya da başka bir anahtarla yüklenmiş eklenti kopyaları için ek kimlikler.
    public List<string> ExtraExtensionIds { get; set; } = [];

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
                LegacyMigration.FixOutputDir(s);
                return s;
            }
        }
        catch { /* bozuk dosya: varsayılanlarla devam */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        Autostart.Apply(StartWithWindows);
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Kaptur";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch { /* izin yoksa sessizce geç */ }
    }
}
