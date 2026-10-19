using System.IO.Compression;
using System.Net.Http.Headers;

namespace Kaptur;

/// yt-dlp, ffmpeg ve uv'yi %LOCALAPPDATA%\Kaptur\tools altına ilk ihtiyaçta resmi GitHub sürümlerinden indirir.
/// PATH'te zaten varsa (ör. winget ile kurulmuşsa) onları kullanır.
static class ToolManager
{
    const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    const string UvUrl = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";

    static readonly HttpClient Http = CreateHttp();
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static string LocalYtDlp => Path.Combine(AppPaths.Tools, "yt-dlp.exe");
    public static string LocalFfmpeg => Path.Combine(AppPaths.Tools, "ffmpeg", "ffmpeg.exe");
    public static string LocalUv => Path.Combine(AppPaths.Tools, "uv", "uv.exe");

    public static string? FindYtDlp() => File.Exists(LocalYtDlp) ? LocalYtDlp : FindOnPath("yt-dlp.exe");
    public static string? FindFfmpeg() => File.Exists(LocalFfmpeg) ? LocalFfmpeg : FindOnPath("ffmpeg.exe");

    public static Task<string> EnsureYtDlpAsync(Action<string> log, CancellationToken ct) =>
        EnsureAsync(FindYtDlp, () => DownloadFileAsync(YtDlpUrl, LocalYtDlp, "yt-dlp", log, ct), ct);

    public static Task<string> EnsureFfmpegAsync(Action<string> log, CancellationToken ct) =>
        EnsureAsync(FindFfmpeg, () => DownloadAndExtractAsync(FfmpegUrl, Path.GetDirectoryName(LocalFfmpeg)!,
            ["ffmpeg.exe", "ffprobe.exe"], "ffmpeg", log, ct), ct);

    public static Task<string> EnsureUvAsync(Action<string> log, CancellationToken ct) =>
        EnsureAsync(() => File.Exists(LocalUv) ? LocalUv : null, () => DownloadAndExtractAsync(UvUrl,
            Path.GetDirectoryName(LocalUv)!, ["uv.exe"], "uv (Python kurucu)", log, ct), ct);

    /// Kendi indirdiğimiz yt-dlp.exe kendini `-U` ile günceller; PATH'teki (winget vb.) kopyaya dokunmayız.
    public static async Task UpdateYtDlpAsync(Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(LocalYtDlp))
        {
            var found = await EnsureYtDlpAsync(log, ct);
            if (found != LocalYtDlp) log($"yt-dlp sistemden kullanılıyor ({found}); onu kendi paket yöneticinizle güncelleyin.");
            return;
        }
        log("yt-dlp güncelleniyor…");
        var code = await ProcessRunner.RunAsync(LocalYtDlp, ["-U"], (l, _) => log(l), ct);
        log(code == 0 ? "yt-dlp güncel." : $"yt-dlp güncellenemedi (çıkış kodu {code}).");
    }

    static async Task<string> EnsureAsync(Func<string?> find, Func<Task> install, CancellationToken ct)
    {
        if (find() is { } path) return path;
        await Gate.WaitAsync(ct);
        try
        {
            if (find() is { } again) return again;
            await install();
            return find() ?? throw new InvalidOperationException("Araç indirildi ama bulunamadı.");
        }
        finally { Gate.Release(); }
    }

    static async Task DownloadFileAsync(string url, string dest, string label, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        log($"{label} indiriliyor…");
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        var tmp = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int lastPct = 0, n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0 && done * 100 / total >= lastPct + 25)
                {
                    lastPct = (int)(done * 100 / total);
                    log($"{label}: %{lastPct} ({done / 1_048_576} / {total / 1_048_576} MB)");
                }
            }
        }
        File.Move(tmp, dest, overwrite: true);
        log($"{label} hazır.");
    }

    static async Task DownloadAndExtractAsync(string url, string destDir, string[] wanted, string label,
        Action<string> log, CancellationToken ct)
    {
        var zipPath = Path.Combine(AppPaths.Tools, Path.GetFileName(new Uri(url).AbsolutePath));
        await DownloadFileAsync(url, zipPath, label, log, ct);
        try
        {
            Directory.CreateDirectory(destDir);
            using var zip = ZipFile.OpenRead(zipPath);
            foreach (var entry in zip.Entries)
                if (wanted.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    entry.ExtractToFile(Path.Combine(destDir, entry.Name), overwrite: true);
        }
        finally { File.Delete(zipPath); }
    }

    public static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kaptur", "0.1"));
        return h;
    }
}
