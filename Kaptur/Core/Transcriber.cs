using System.Text.Json;

namespace Kaptur;

sealed class TranscribeException(string message, string? device) : Exception(message)
{
    public string? Device { get; } = device;

    /// Süreç JSON hata olayı yazmadan öldüyse (DLL bulunamadı vb. yerel çökme) ya da hata CUDA kaynaklıysa.
    public bool LooksLikeGpuFailure { get; init; }
}

/// faster-whisper'ı uygulamanın kendi klasöründeki bir Python ortamında çalıştırır.
/// Sisteme Python kurulmaz: uv, Python 3.12'yi ve paketleri %LOCALAPPDATA%\Kaptur\tools altına koyar.
static class Transcriber
{
    public sealed record Result(string TxtPath, string SrtPath, string Language, string Device, int Segments);

    static readonly SemaphoreSlim InstallGate = new(1, 1);
    static readonly SemaphoreSlim GpuGate = new(1, 1);   // aynı anda tek transcript: 6 GB VRAM paylaşılmasın

    static string EnvDir => Path.Combine(AppPaths.Tools, "whisper-env");
    static string Python => Path.Combine(EnvDir, "Scripts", "python.exe");
    // Paket listesi değiştiğinde sürümü artır: eski ortam silinip yeniden kurulur.
    static string Marker => Path.Combine(EnvDir, "kaptur-ready-v2.txt");
    static string ScriptPath => Path.Combine(AppPaths.Tools, "transcribe.py");

    public static bool IsInstalled => File.Exists(Marker) && File.Exists(Python);
    public static bool HasNvidiaGpu => File.Exists(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"));

    static readonly string[] MediaExtensions = [".mp4", ".mkv", ".webm", ".mov", ".m4a", ".mp3", ".wav", ".ogg", ".opus", ".aac", ".flac"];
    public static bool IsMedia(string path) => MediaExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    static Dictionary<string, string> Env() => new()
    {
        ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(AppPaths.Tools, "python"),
        ["UV_CACHE_DIR"] = Path.Combine(AppPaths.Tools, "uv-cache"),
        ["UV_NO_PROGRESS"] = "1",
        ["UV_NO_CONFIG"] = "1",   // kullanıcının kendi uv.toml'u araya girmesin
        ["HF_HOME"] = Path.Combine(AppPaths.Tools, "hf"),
        ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        ["HF_HUB_DISABLE_TELEMETRY"] = "1",
    };

    public static async Task EnsureInstalledAsync(Action<string> log, CancellationToken ct)
    {
        if (IsInstalled) return;
        await InstallGate.WaitAsync(ct);
        try
        {
            if (IsInstalled) return;
            var uv = await ToolManager.EnsureUvAsync(log, ct);
            if (Directory.Exists(EnvDir)) Directory.Delete(EnvDir, recursive: true);   // yarım kalmış kurulum

            // --no-bin / --no-registry: ~/.local/bin'e python.exe koyma, Windows kayıt defterine Python kaydetme;
            // her şey tools klasöründe kalsın. --managed-python: sistemdeki başka bir Python'u seçme.
            log("Python 3.12 indiriliyor…");
            await RunUv(uv, ["python", "install", "3.12", "--no-bin", "--no-registry"], log, ct, "Python kurulamadı");
            log("Python ortamı hazırlanıyor…");
            await RunUv(uv, ["venv", "--python", "3.12", "--managed-python", "--no-python-downloads", EnvDir],
                log, ct, "Python ortamı kurulamadı");

            var install = new List<string> { "pip", "install", "--python", Python, "faster-whisper" };
            if (HasNvidiaGpu) install.AddRange(["nvidia-cublas-cu12", "nvidia-cudnn-cu12==9.*"]);
            log(HasNvidiaGpu
                ? "faster-whisper ve CUDA kütüphaneleri kuruluyor (~1,5 GB, birkaç dakika sürebilir)…"
                : "faster-whisper kuruluyor…");
            await RunUv(uv, install, log, ct, "faster-whisper kurulamadı");

            File.WriteAllText(Marker, DateTime.Now.ToString("O"));
            log("Transcript bileşenleri hazır.");
        }
        finally { InstallGate.Release(); }
    }

    static async Task RunUv(string uv, IEnumerable<string> args, Action<string> log, CancellationToken ct, string failure)
    {
        var code = await ProcessRunner.RunAsync(uv, args, (l, _) => { if (l.Trim().Length > 0) log(l); }, ct, Env());
        if (code != 0) throw new TranscribeException($"{failure} (uv çıkış kodu {code}). Ayrıntı günlükte.", null);
    }

    public static async Task<Result> TranscribeAsync(string media, AppSettings s, Action<double, string> onProgress,
        Action<string> log, CancellationToken ct)
    {
        await EnsureInstalledAsync(log, ct);
        WriteScript();
        if (GpuGate.CurrentCount == 0) onProgress(-1, "Sırada (başka bir transcript sürüyor)…");
        await GpuGate.WaitAsync(ct);
        try
        {
            try
            {
                return await RunOnce(media, s, s.WhisperDevice, onProgress, log, ct);
            }
            catch (TranscribeException ex) when (ex.Device == "cuda" && s.WhisperDevice != "cpu" && ex.LooksLikeGpuFailure)
            {
                // cuDNN/cuBLAS DLL'i eksikse süreç yerel olarak çöker; Python içinde yakalanamaz. CPU ile yeniden dene.
                log($"GPU ile transcript başarısız ({ex.Message}); CPU ile tekrar deneniyor…");
                return await RunOnce(media, s, "cpu", onProgress, log, ct);
            }
        }
        finally { GpuGate.Release(); }
    }

    static async Task<Result> RunOnce(string media, AppSettings s, string device, Action<double, string> onProgress,
        Action<string> log, CancellationToken ct)
    {
        var outBase = Path.Combine(Path.GetDirectoryName(media)!, Path.GetFileNameWithoutExtension(media));
        string? usedDevice = null, error = null, txt = null, srt = null, lang = null;
        int segments = 0;
        var gate = new object();

        void Handle(string line, bool isErr)
        {
            if (!line.StartsWith('{')) { if (line.Trim().Length > 0) log(line); return; }
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                string Str(string k) => r.TryGetProperty(k, out var v) ? v.ToString() : "";
                switch (Str("event"))
                {
                    case "loading":
                        lock (gate) usedDevice = Str("device");
                        onProgress(-1, $"Model yükleniyor ({Str("model")}, {(Str("device") == "cuda" ? "GPU" : "CPU")})… ilk seferde indirilir");
                        break;
                    case "start":
                        lock (gate) lang = Str("language");
                        onProgress(0, $"Yazıya dökülüyor · dil: {Str("language")}");
                        break;
                    case "progress":
                        if (r.TryGetProperty("pct", out var p)) onProgress(p.GetDouble(), $"Yazıya dökülüyor · dil: {lang}");
                        break;
                    case "done":
                        lock (gate) { txt = Str("txt"); srt = Str("srt"); lang = Str("language"); int.TryParse(Str("segments"), out segments); }
                        break;
                    case "error":
                        lock (gate) error = Str("message");
                        break;
                }
            }
            catch (JsonException) { log(line); }
        }

        var args = new List<string>
        {
            "-I", ScriptPath, media, "--out-base", outBase,
            "--model", s.WhisperModel, "--language", s.WhisperLanguage, "--device", device,
        };
        var code = await ProcessRunner.RunAsync(Python, args, Handle, ct, Env());
        if (code != 0 || txt == null)
            throw new TranscribeException(error ?? $"Python {code} koduyla çıktı.", usedDevice)
            {
                LooksLikeGpuFailure = error == null
                    || new[] { "cuda", "cudnn", "cublas", ".dll" }.Any(k => error.Contains(k, StringComparison.OrdinalIgnoreCase)),
            };
        return new Result(txt, srt!, lang ?? "", usedDevice ?? device, segments);
    }

    static void WriteScript()
    {
        using var res = typeof(Transcriber).Assembly.GetManifestResourceStream("Kaptur.transcribe.py")
                        ?? throw new InvalidOperationException("transcribe.py gömülü kaynağı bulunamadı.");
        using var reader = new StreamReader(res);
        var content = reader.ReadToEnd();
        Directory.CreateDirectory(AppPaths.Tools);
        if (!File.Exists(ScriptPath) || File.ReadAllText(ScriptPath) != content)
            File.WriteAllText(ScriptPath, content);
    }
}
