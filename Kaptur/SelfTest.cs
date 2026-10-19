namespace Kaptur;

/// Geliştirme için arayüzsüz denemeler. Çıktı %LOCALAPPDATA%\Kaptur\kaptur.log'a yazılır.
static class SelfTest
{
    public static int Run(string source, bool transcribe)
    {
        var settings = AppSettings.Load();
        var jobs = new JobManager(settings, new SynchronizationContext());   // olaylar thread pool'da çalışır
        jobs.WriteLog($"=== selftest: {source} (transcript: {transcribe})");
        string last = "";
        jobs.JobChanged += j =>
        {
            // Hız/yüzde her satırda değişir; yalnızca aşama değişince yaz.
            var key = $"{j.State}: {j.Status.Split('·')[0].Trim()}";
            if (key != last) jobs.WriteLog($"[#{j.Id}] {j.State}: {j.Status}");
            last = key;
        };
        var job = File.Exists(source) ? jobs.AddLocalFile(source) : jobs.AddUrl(source, transcribe);
        job!.Completion.GetAwaiter().GetResult();
        Thread.Sleep(200);   // son Post edilen olayların günlüğe düşmesi için
        jobs.WriteLog($"=== sonuç: {job.State} · dosyalar: {string.Join(" | ", job.Files)} · transcript: {string.Join(" | ", job.Transcripts)}");
        return job.State == JobState.Done ? 0 : 1;
    }

    /// Eklenti simgelerini (16/32/48/128 px PNG) üretir.
    public static int ExportIcons(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var size in new[] { 16, 32, 48, 128 })
        {
            using var bmp = AppIcon.Render(size);
            bmp.Save(Path.Combine(dir, $"icon{size}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        WriteIco(Path.Combine(dir, "kaptur.ico"), [16, 20, 24, 32, 40, 48, 64, 128, 256]);
        return 0;
    }

    /// Çok boyutlu .ico: her boyut PNG olarak gömülür (Vista'dan beri desteklenir).
    static void WriteIco(string path, int[] sizes)
    {
        var images = sizes.Select(s =>
        {
            using var bmp = AppIcon.Render(s);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return (Size: s, Data: ms.ToArray());
        }).ToList();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) w.Write(data);
    }

    public static int Screenshot(string png, bool settingsDialog)
    {
        var settings = AppSettings.Load();
        using Form f = settingsDialog
            ? new SettingsForm(settings)
            : new MainForm(settings, new JobManager(settings, new WindowsFormsSynchronizationContext()));
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-5000, -5000);
        f.Shown += (_, _) =>
        {
            Application.DoEvents();
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
            bmp.Save(png);
            f.BeginInvoke(() => { if (f is MainForm m) m.AllowClose = true; f.Close(); });
        };
        Application.Run(f);
        return 0;
    }
}
