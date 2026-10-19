using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Kaptur;

/// Chrome (ve Chromium tabanlı Edge/Brave) eklentisiyle ilgili kurulum işleri:
/// eklenti dosyalarını sabit bir klasöre koymak ve native messaging host'unu kayıt defterine yazmak.
static class ChromeIntegration
{
    /// Eklenti manifest'indeki "key" alanından türeyen sabit kimlik: eklenti hangi klasörden yüklenirse yüklensin aynı kalır.
    public const string ExtensionId = "hjigcpdeclnpobinaoklbjnfmkhangka";
    public const string HostName = "com.kaptur.host";

    public static string ExtensionDir => Path.Combine(AppPaths.Root, "ChromeExtension");
    static string BundledExtensionDir => Path.Combine(AppContext.BaseDirectory, "Extension");
    static string HostManifestPath => Path.Combine(AppPaths.Root, "native-host", HostName + ".json");

    /// HKCU altında her tarayıcının native messaging host'larını aradığı yer. Chrome dışındakiler yalnızca kuruluysa yazılır.
    internal static readonly string[] BrowserRoots =
    [
        @"Software\Google\Chrome",
        @"Software\Microsoft\Edge",
        @"Software\BraveSoftware\Brave-Browser",
        @"Software\Chromium",
    ];

    public static IEnumerable<string> AllowedIds(AppSettings s) =>
        new[] { ExtensionId }.Concat(s.ExtraExtensionIds.Where(IsValidId)).Distinct();

    public static bool IsAllowedOrigin(string origin, AppSettings s) =>
        AllowedIds(s).Any(id => origin.TrimEnd('/') == "chrome-extension://" + id);

    /// Chromium uzantı kimliği: a–p arası 32 harf.
    public static bool IsValidId(string id) => id.Length == 32 && id.All(c => c is >= 'a' and <= 'p');

    /// Uygulamayla gelen eklenti dosyalarını %LOCALAPPDATA%\Kaptur\ChromeExtension'a kopyalar. Chrome
    /// "paketlenmemiş" eklentiyi yüklendiği klasörden okumaya devam ettiği için uygulama taşınsa da yol değişmez.
    public static void SyncExtensionFolder(Action<string> log)
    {
        if (!Directory.Exists(BundledExtensionDir)) return;
        try
        {
            foreach (var src in Directory.EnumerateFiles(BundledExtensionDir, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(ExtensionDir, Path.GetRelativePath(BundledExtensionDir, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst) || !File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(dst)))
                    File.Copy(src, dst, overwrite: true);
            }
        }
        catch (Exception ex) { log($"Eklenti klasörü güncellenemedi: {ex.Message}"); }
    }

    /// Native messaging host manifest'ini yazar ve tarayıcılara kaydeder. Her açılışta yapılır, böylece
    /// Kaptur.exe taşınırsa yol güncellenir. Yalnızca HKCU: yönetici izni gerekmez.
    public static void RegisterNativeHost(AppSettings s, Action<string> log)
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HostManifestPath)!);
            var manifest = new JsonObject
            {
                ["name"] = HostName,
                ["description"] = "Kaptur başlatıcı",
                ["path"] = exe,
                ["type"] = "stdio",
                ["allowed_origins"] = new JsonArray(AllowedIds(s).Select(id => (JsonNode)$"chrome-extension://{id}/").ToArray()),
            };
            File.WriteAllText(HostManifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            foreach (var root in BrowserRoots)
            {
                using (var existing = Registry.CurrentUser.OpenSubKey(root))
                    if (existing == null && root != BrowserRoots[0]) continue;
                using var key = Registry.CurrentUser.CreateSubKey($@"{root}\NativeMessagingHosts\{HostName}");
                key.SetValue("", HostManifestPath);
            }
        }
        catch (Exception ex) { log($"Native messaging host kaydedilemedi: {ex.Message}"); }
    }

    public static string SetupInstructions =>
        "Chrome eklentisini kurmak için:\n\n" +
        "1. Chrome'da adres çubuğuna chrome://extensions yazın (adres panoya kopyalandı).\n" +
        "2. Sağ üstten \"Geliştirici modu\"nu açın.\n" +
        "3. \"Paketlenmemiş öğe yükle\"ye tıklayıp şu klasörü seçin (Tamam'a basınca açılacak):\n\n" +
        $"{ExtensionDir}\n\n" +
        "Edge'de edge://extensions, Brave'de brave://extensions adresini kullanın.\n" +
        "Kurulumdan sonra eklentiyi araç çubuğuna sabitleyin. Kaptur kapalıysa eklenti onu kendisi başlatır.";
}
