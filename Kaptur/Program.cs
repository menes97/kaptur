namespace Kaptur;

static class Program
{
    // Tek örnek kilidi veri klasörüne bağlı: KAPTUR_HOME ile ayrı klasör kullanan kopya ayrı bir örnek sayılır.
    static readonly string InstanceKey = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..12];
    internal static readonly string MutexName = $@"Local\Kaptur.{InstanceKey}";
    internal static readonly string ShowEventName = $@"Local\Kaptur.{InstanceKey}.Show";

    /// Argümanlar:
    ///   --tray                         pencereyi açmadan tepside başla (Windows ile başlat bunu kullanır)
    ///   --selftest &lt;url|dosya&gt; [--transcribe]   arayüzsüz uçtan uca deneme; çıktı kaptur.log'a yazılır
    ///   --screenshot &lt;png&gt; [--settings]       ana pencereyi (veya ayarları) PNG'ye çizip çıkar
    [STAThread]
    static int Main(string[] args)
    {
        // Chrome bizi native messaging host olarak çağırdı: pencere yok, yalnızca stdin/stdout.
        if (NativeHost.IsInvocation(args)) return NativeHost.Run();

        ApplicationConfiguration.Initialize();
        if (args.Length >= 2 && args[0] == "--export-icons")
            return SelfTest.ExportIcons(args[1]);

        if (args.Length >= 2 && args[0] == "--selftest")
            return SelfTest.Run(args[1], args.Contains("--transcribe"));
        if (args.Length >= 2 && args[0] == "--screenshot")
            return SelfTest.Screenshot(args[1], args.Contains("--settings"));

        using var mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // Zaten çalışan örneğe "pencereni göster" de ve çık.
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev)) { ev.Set(); ev.Dispose(); }
            return 0;
        }

        LegacyMigration.Run();   // eski adı ReelGrab olan sürümden kalanlar
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        Application.Run(new TrayContext(showWindow: !args.Contains("--tray")));
        return 0;
    }
}
