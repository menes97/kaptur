using Microsoft.Win32;

namespace Kaptur;

/// Uygulamanın eski adı ReelGrab'di. O sürümden kalanları bir kez Kaptur'a taşır:
/// veri klasörü, Windows ile başlat kaydı ve Chrome native messaging kayıtları.
static class LegacyMigration
{
    const string OldName = "ReelGrab";
    const string OldHostName = "com.reelgrab.host";

    public static void Run()
    {
        // Ayrı bir veri klasörüyle (KAPTUR_HOME) çalışan kopyalar kullanıcının asıl kurulumuna dokunmasın.
        if (Environment.GetEnvironmentVariable("KAPTUR_HOME") is { Length: > 0 }) return;
        MoveDataFolder();
        MoveAutostart();
        RemoveOldNativeHost();
    }

    static void MoveDataFolder()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName);
        if (!Directory.Exists(old) || Directory.Exists(AppPaths.Root)) return;
        try
        {
            Directory.Move(old, AppPaths.Root);
            // uv'nin Python kurulumu ve sanal ortam mutlak yollar (ve bir dizin bağlantısı) içerir; taşınınca bozulur.
            // İlk transcriptte yeniden kurulur, paketler tools\uv-cache'ten geldiği için saniyeler sürer.
            foreach (var dir in new[] { "whisper-env", "python" })
            {
                var path = Path.Combine(AppPaths.Tools, dir);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            var oldLog = Path.Combine(AppPaths.Root, "reelgrab.log");
            if (File.Exists(oldLog) && !File.Exists(AppPaths.LogFile)) File.Move(oldLog, AppPaths.LogFile);
            var oldManifest = Path.Combine(AppPaths.Root, "native-host", OldHostName + ".json");
            if (File.Exists(oldManifest)) File.Delete(oldManifest);
        }
        catch { /* taşınamazsa yeni klasörle sıfırdan devam edilir */ }
    }

    /// Kullanıcı "Windows ile başlat"ı açtıysa kayıt yeni adla, bu exe'yi gösterecek şekilde yeniden yazılır.
    static void MoveAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key?.GetValue(OldName) == null) return;
            key.DeleteValue(OldName, throwOnMissingValue: false);
            Autostart.Apply(true);
        }
        catch { }
    }

    static void RemoveOldNativeHost()
    {
        foreach (var root in ChromeIntegration.BrowserRoots)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{root}\NativeMessagingHosts\{OldHostName}", throwOnMissingSubKey: false); }
            catch { }
        }
    }

    /// Ayarlarda eski varsayılan indirme klasörü (Videolar\ReelGrab) kaldıysa yeni varsayılana çevrilir.
    public static void FixOutputDir(AppSettings s)
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.Equals(s.OutputDir, Path.Combine(videos, OldName), StringComparison.OrdinalIgnoreCase))
            s.OutputDir = Path.Combine(videos, "Kaptur");
    }
}
