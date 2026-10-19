using System.Globalization;

namespace Kaptur;

sealed class DownloadException(string message) : Exception(message);

/// yt-dlp sarmalayıcısı. mc-download-manager'daki VideoDownloader.swift ile aynı yaklaşım: makine-okunur
/// --progress-template ve --print satırları basılır, gerisi günlüğe gider.
static class Downloader
{
    public sealed record Result(List<string> Files, string Title);

    public static async Task<Result> DownloadAsync(string url, AppSettings s, DownloadOptions? opts, string ytdlp, string? ffmpeg,
        Action<double, string> onProgress, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(s.OutputDir);
        var args = new List<string>
        {
            "--newline", "--no-colors", "--no-mtime", "--encoding", "utf-8",
            "--progress", "--no-simulate", "--no-quiet", "-N", "4",
            "-P", s.OutputDir,
            "-o", "%(extractor_key)s - %(uploader_id,uploader|unknown)s - %(title).60B [%(id)s].%(ext)s",
            "--trim-filenames", "180",
            // Windows'un yerleşik oynatıcısında sorunsuz açılsın diye H.264 + AAC tercih, en fazla 1080p, MP4 kapsayıcı.
            "-f", "bv*+ba/b", "-S", "vcodec:h264,res:1080,acodec:aac", "--merge-output-format", "mp4",
            "--progress-template",
            "download:RGP|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s",
            "--print", "before_dl:RGINFO|%(title)s",
            "--print", "after_move:RGFILE|%(filepath)s",
        };
        if (ffmpeg != null) args.AddRange(["--ffmpeg-location", ffmpeg]);
        if (!string.IsNullOrWhiteSpace(opts?.UserAgent)) args.AddRange(["--user-agent", opts.UserAgent]);
        if (!string.IsNullOrWhiteSpace(opts?.Referer)) args.AddRange(["--referer", opts.Referer]);
        // Eklentinin gönderdiği çerezler (tarayıcıdaki oturum) ayarlardaki çerez kaynağından önce gelir.
        var hasCookies = opts?.CookieFile is { } cf && File.Exists(cf);
        if (hasCookies) args.AddRange(["--cookies", opts!.CookieFile!]);
        else switch (s.Cookies)
        {
            case CookieSource.Firefox:
                args.AddRange(["--cookies-from-browser", "firefox"]);
                break;
            case CookieSource.File when File.Exists(s.CookiesFile):
                args.AddRange(["--cookies", s.CookiesFile]);
                break;
        }
        args.Add(url);

        var files = new List<string>();
        var title = "";
        string? lastError = null;
        var gate = new object();

        void Handle(string raw, bool isErr)
        {
            var line = raw.Trim();
            if (line.Length == 0) return;
            if (line.StartsWith("RGP|"))
            {
                var p = line.Split('|');
                if (p.Length < 5) return;
                double done = Num(p[1]), total = Num(p[2]);
                if (total <= 0) total = Num(p[3]);
                var speed = Num(p[4]);
                var pct = total > 0 ? Math.Clamp(done / total, 0, 1) : -1;
                var text = speed > 0 ? $"İndiriliyor · {FormatSize(speed)}/sn" : "İndiriliyor…";
                onProgress(pct, text);
                return;
            }
            lock (gate)
            {
                if (line.StartsWith("RGINFO|")) { if (title.Length == 0) title = line[7..].Trim(); return; }
                if (line.StartsWith("RGFILE|")) { var f = line[7..].Trim(); if (File.Exists(f) && !files.Contains(f)) files.Add(f); return; }
                if (line.StartsWith("ERROR:")) lastError = line[6..].Trim();
                // "[download] C:\…\x.mp4 has already been downloaded"
                const string already = " has already been downloaded";
                if (line.StartsWith("[download] ") && line.EndsWith(already))
                {
                    var f = line["[download] ".Length..^already.Length].Trim();
                    if (File.Exists(f) && !files.Contains(f)) files.Add(f);
                }
            }
            if (line.StartsWith("[Merger]")) onProgress(-1, "Ses ve görüntü birleştiriliyor…");
            log(line);
        }

        var code = await ProcessRunner.RunAsync(ytdlp, args, Handle, ct);
        if (files.Count == 0)
            throw new DownloadException(Explain(url, lastError ?? $"yt-dlp {code} koduyla çıktı ve dosya üretmedi.", hasCookies || s.Cookies != CookieSource.None));
        // Gönderide birden çok medya varsa (ör. video + gömülü Vimeo kartı) biri başarısız olsa da inenleri koru.
        if (code != 0) log($"Uyarı: bazı medya öğeleri indirilemedi: {lastError}");
        return new Result(files, title);
    }

    /// Bilinen hata türlerine Türkçe bir ipucu ekler.
    static string Explain(string url, string error, bool usedCookies)
    {
        var e = error.ToLowerInvariant();
        bool authLike = e.Contains("login") || e.Contains("cookie") || e.Contains("rate-limit") || e.Contains("rate limit")
                        || e.Contains("not available") || e.Contains("empty media response") || e.Contains("authentication")
                        || e.Contains("nsfw") || e.Contains("age-restricted") || e.Contains("age restricted")
                        || e.Contains("confirm your age");
        if (authLike && !usedCookies)
            return error + (LinkDetector.IsInstagram(url)
                ? "\nİpucu: Instagram genelde giriş ister. Videoyu Chrome eklentisinden indirin (tarayıcıdaki oturumunuz kullanılır) ya da Ayarlar > Çerez kaynağı'nı ayarlayın."
                : "\nİpucu: Bu gönderi giriş gerektiriyor olabilir. Chrome eklentisinden indirmeyi ya da Ayarlar > Çerez kaynağı'nı deneyin.");
        if (e.Contains("could not copy") && e.Contains("cookie"))
            return error + "\nİpucu: Firefox açıkken çerez veritabanı kilitli olabilir; Firefox'u kapatıp tekrar deneyin.";
        if (e.Contains("unsupported url"))
            return error + "\nİpucu: Bu link biçimi tanınmadı. Gönderinin tam linkini (…/reel/… veya …/status/…) kullanın.";
        return error;
    }

    static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : -1;

    public static string FormatSize(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {units[i]}";
    }
}
