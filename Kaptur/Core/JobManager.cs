namespace Kaptur;

enum JobState { Queued, Preparing, Downloading, Transcribing, Done, Failed, Canceled }

/// Tarayıcı eklentisinden gelen indirmeye özel bilgiler. CookieFile, indirme bitince silinir.
sealed record DownloadOptions(string? CookieFile, string? UserAgent, string? Referer);

sealed class Job
{
    static int nextId;
    public int Id { get; } = Interlocked.Increment(ref nextId);
    public required string Source { get; init; }          // link ya da yerel dosya
    public bool IsLocalFile { get; init; }
    public bool Transcribe { get; set; }
    public DownloadOptions? Options { get; set; }
    public JobState State { get; set; } = JobState.Queued;
    public double Progress { get; set; } = -1;            // 0..1; -1 = belirsiz
    public string Status { get; set; } = "Sırada";
    public string Title { get; set; } = "";
    public List<string> Files { get; } = [];
    public List<string> Transcripts { get; } = [];
    public CancellationTokenSource Cts { get; private set; } = new();
    readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Completion => done.Task;

    public bool IsActive => State is JobState.Queued or JobState.Preparing or JobState.Downloading or JobState.Transcribing;
    public string SourceLabel => IsLocalFile ? "Dosya" : LinkDetector.SiteName(Source);

    internal void Finish() => done.TrySetResult();
    internal void ResetForRerun() { Cts = new(); }
}

/// İndirme ve transcript kuyruğu. Tüm Job alanı değişiklikleri ve olaylar UI bağlamında yapılır,
/// böylece arayüz kilitsiz okuyabilir.
sealed class JobManager(AppSettings settings, SynchronizationContext ui)
{
    readonly List<Job> jobs = [];
    readonly SemaphoreSlim downloadSlots = new(Math.Max(1, settings.MaxParallelDownloads));

    public event Action<Job>? JobAdded;
    public event Action<Job>? JobChanged;
    public event Action<Job>? JobRemoved;
    public event Action<string>? Log;
    /// başlık, metin, hata mı, tıklanınca açılacak dosya
    public event Action<string, string, bool, string?>? Notify;

    public IReadOnlyList<Job> Jobs => jobs;

    public bool HasActive(string source) => jobs.Any(j => j.IsActive && j.Source == source);

    public Job? AddUrl(string url, bool transcribe, DownloadOptions? options = null)
    {
        if (HasActive(url)) return null;
        var job = new Job { Source = url, Transcribe = transcribe, Options = options };
        jobs.Add(job);
        JobAdded?.Invoke(job);
        _ = RunAsync(job);
        return job;
    }

    public Job? AddLocalFile(string path)
    {
        if (HasActive(path)) return null;
        var job = new Job { Source = path, IsLocalFile = true, Transcribe = true, Title = Path.GetFileName(path) };
        job.Files.Add(path);
        jobs.Add(job);
        JobAdded?.Invoke(job);
        _ = RunAsync(job);
        return job;
    }

    /// İndirilmiş ama transcripti olmayan bir iş için sonradan transcript çıkarır.
    public void TranscribeExisting(Job job)
    {
        if (job.IsActive || job.Files.Count == 0) return;
        job.Transcribe = true;
        job.Transcripts.Clear();
        job.ResetForRerun();
        _ = RunAsync(job, skipDownload: true);
    }

    public void Retry(Job job)
    {
        if (job.IsActive) return;
        job.ResetForRerun();
        _ = RunAsync(job, skipDownload: job.Files.Count > 0);
    }

    public void Cancel(Job job) => job.Cts.Cancel();

    public void Remove(Job job)
    {
        if (job.IsActive) job.Cts.Cancel();
        jobs.Remove(job);
        JobRemoved?.Invoke(job);
    }

    public void WriteLog(string line)
    {
        try { File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}"); } catch { }
        ui.Post(_ => Log?.Invoke(line), null);
    }

    void Update(Job job, JobState state, string status, double progress) => ui.Post(_ =>
    {
        job.State = state;
        job.Status = status;
        job.Progress = progress;
        JobChanged?.Invoke(job);
    }, null);

    void Post(Action a) => ui.Post(_ => a(), null);

    async Task RunAsync(Job job, bool skipDownload = false)
    {
        var ct = job.Cts.Token;
        var tag = $"[#{job.Id}]";
        var files = job.Files.ToList();   // Post edilen güncellemeleri beklemeden okuyabilmek için yerel kopya
        var spoken = 0;   // konuşma bulunan transcript sayısı
        var options = job.Options;
        try
        {
            if (!job.IsLocalFile && !skipDownload)
            {
                Update(job, JobState.Queued, "Sırada", -1);
                await downloadSlots.WaitAsync(ct);
                try
                {
                    Update(job, JobState.Preparing, "Araçlar hazırlanıyor…", -1);
                    var ytdlp = await ToolManager.EnsureYtDlpAsync(WriteLog, ct);
                    var ffmpeg = await ToolManager.EnsureFfmpegAsync(WriteLog, ct);
                    Update(job, JobState.Downloading, "İndiriliyor…", -1);
                    WriteLog($"{tag} İndiriliyor: {job.Source}");
                    var r = await Downloader.DownloadAsync(job.Source, settings, options, ytdlp, ffmpeg,
                        (pct, text) => Update(job, JobState.Downloading, text, pct), l => WriteLog($"{tag} {l}"), ct);
                    files = r.Files;
                    Post(() =>
                    {
                        job.Files.Clear();
                        job.Files.AddRange(r.Files);
                        if (r.Title.Length > 0) job.Title = r.Title;
                    });
                    foreach (var f in r.Files) WriteLog($"{tag} Kaydedildi: {f}");
                }
                finally
                {
                    downloadSlots.Release();
                    // Eklentiden gelen çerezler hesaba erişim sağlar; diskte gereğinden uzun kalmasın.
                    if (options?.CookieFile is { } cookieFile)
                    {
                        try { File.Delete(cookieFile); } catch { }
                        Post(() => job.Options = options with { CookieFile = null });
                    }
                }
            }

            if (job.Transcribe)
            {
                var media = files.Where(Transcriber.IsMedia).ToList();
                if (media.Count == 0) throw new InvalidOperationException("Transcript çıkarılacak video/ses dosyası yok.");
                foreach (var (file, i) in media.Select((f, i) => (f, i)))
                {
                    var prefix = media.Count > 1 ? $"[{i + 1}/{media.Count}] " : "";
                    Update(job, JobState.Transcribing, prefix + "Transcript hazırlanıyor…", -1);
                    var t = await Transcriber.TranscribeAsync(file, settings,
                        (pct, text) => Update(job, JobState.Transcribing, prefix + text, pct),
                        l => WriteLog($"{tag} {l}"), ct);
                    Post(() => job.Transcripts.Add(t.TxtPath));
                    if (t.Segments > 0) spoken++;
                    WriteLog($"{tag} Transcript ({t.Language}, {t.Device}): {t.TxtPath}");
                }
            }

            ui.Post(_ =>
            {
                job.State = JobState.Done;
                job.Progress = 1;
                job.Status = job.Transcripts.Count == 0 ? "Tamamlandı"
                    : spoken > 0 ? "Tamamlandı · transcript hazır"
                    : "Tamamlandı · videoda konuşma algılanmadı";
                JobChanged?.Invoke(job);
                var name = job.Title.Length > 0 ? job.Title : Path.GetFileName(job.Files.FirstOrDefault() ?? job.Source);
                Notify?.Invoke(job.Transcripts.Count > 0 ? "Transcript hazır" : "İndirme tamamlandı", name, false,
                    job.Transcripts.FirstOrDefault() ?? job.Files.FirstOrDefault());
                job.Finish();
            }, null);
        }
        catch (OperationCanceledException)
        {
            Update(job, JobState.Canceled, "İptal edildi", -1);
            WriteLog($"{tag} İptal edildi.");
            Post(job.Finish);
        }
        catch (Exception ex)
        {
            var msg = ex.Message;
            Update(job, JobState.Failed, "Hata: " + msg.Split('\n')[0], -1);
            WriteLog($"{tag} HATA: {msg}");
            Post(() =>
            {
                Notify?.Invoke("İşlem başarısız", msg, true, null);
                job.Finish();
            });
        }
        finally
        {
            // Sıradayken iptal edilen işler indirme bloğuna hiç girmez; çerez dosyası yine de silinsin.
            if (options?.CookieFile is { } cf && File.Exists(cf)) try { File.Delete(cf); } catch { }
        }
    }
}
