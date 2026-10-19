namespace Kaptur;

/// Uygulamanın asıl sahibi: tepsi simgesi, ana pencere, pano izleyici ve iş kuyruğu.
sealed class TrayContext : ApplicationContext
{
    readonly AppSettings settings = AppSettings.Load();
    readonly SynchronizationContext ui = SynchronizationContext.Current!;
    readonly JobManager jobs;
    readonly MainForm form;
    readonly NotifyIcon tray;
    readonly ClipboardMonitor clipboard = new();
    readonly Bridge bridge;
    readonly ToolStripMenuItem watchItem;
    readonly EventWaitHandle showEvent = new(false, EventResetMode.AutoReset, Program.ShowEventName);
    readonly Icon idleIcon = AppIcon.Create(), busyIcon = AppIcon.Create(busy: true);

    Action? balloonAction;
    string lastClipUrl = "";
    DateTime lastClipAt;
    bool hintedTray;

    public TrayContext(bool showWindow)
    {
        Directory.CreateDirectory(AppPaths.Root);
        jobs = new JobManager(settings, ui);
        form = new MainForm(settings, jobs);
        form.SettingsRequested += ShowSettings;
        form.InstallTranscriberRequested += () => RunTool("Transcript bileşenleri", Transcriber.EnsureInstalledAsync);
        form.UpdateYtDlpRequested += () => RunTool("yt-dlp", ToolManager.UpdateYtDlpAsync);

        // Chrome eklentisi: dosyaları sabit klasöre koy, native host'u kaydet (exe taşındıysa yol güncellenir), köprüyü aç.
        bridge = new Bridge(jobs, settings, ui, ShowMain);
        form.BridgeStatus = () => bridge.Status;
        ChromeIntegration.SyncExtensionFolder(jobs.WriteLog);
        ChromeIntegration.RegisterNativeHost(settings, jobs.WriteLog);
        bridge.Start();
        form.RefreshStatus();
        form.VisibleChanged += (_, _) =>
        {
            if (form.Visible || hintedTray) return;
            hintedTray = true;
            ShowBalloon("Kaptur tepside çalışıyor", "Instagram/X linki kopyaladığınızda size soracağım. Çıkmak için tepsi simgesine sağ tıklayın.", ToolTipIcon.Info, null);
        };

        watchItem = new ToolStripMenuItem("Panoyu izle", null, (_, _) =>
        {
            settings.WatchClipboard = !settings.WatchClipboard;
            settings.Save();
            SyncMenu();
        });
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Kaptur'u aç", null, (_, _) => ShowMain()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.AddRange(
        [
            open,
            new ToolStripMenuItem("Panodaki linki indir", null, (_, _) => DownloadClipboard(transcribe: false)),
            new ToolStripMenuItem("Panodaki linki indir + transcript", null, (_, _) => DownloadClipboard(transcribe: true)),
            new ToolStripSeparator(),
            watchItem,
            new ToolStripMenuItem("İndirme klasörünü aç", null, (_, _) => Kaptur.MainForm.OpenFolder(settings.OutputDir)),
            new ToolStripMenuItem("Chrome eklentisini kur…", null, (_, _) => Kaptur.MainForm.ShowExtensionSetup(null)),
            new ToolStripMenuItem("Ayarlar…", null, (_, _) => ShowSettings()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Çıkış", null, (_, _) => Exit()),
        ]);

        tray = new NotifyIcon { Icon = idleIcon, Text = "Kaptur", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        tray.BalloonTipClicked += (_, _) => { var a = balloonAction; balloonAction = null; a?.Invoke(); };
        tray.BalloonTipClosed += (_, _) => balloonAction = null;

        clipboard.TextCopied += OnClipboardText;
        jobs.Notify += (title, text, isError, file) =>
            ShowBalloon(title, Shorten(text, 200), isError ? ToolTipIcon.Error : ToolTipIcon.Info,
                isError ? (Action)ShowMain : file != null ? () => Kaptur.MainForm.Reveal(file) : null);
        jobs.JobChanged += _ => UpdateBusy();
        jobs.JobAdded += _ => UpdateBusy();

        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ui.Post(_ => ShowMain(), null), null, -1, executeOnlyOnce: false);
        SyncMenu();
        if (showWindow) ShowMain();
    }

    void OnClipboardText(string text)
    {
        if (!settings.WatchClipboard) return;
        var url = LinkDetector.ExtractSocial(text).FirstOrDefault();
        if (url == null || jobs.HasActive(url)) return;
        // Bazı uygulamalar panoya art arda birden çok kez yazar; aynı linki 3 sn içinde tekrar sorma.
        if (url == lastClipUrl && DateTime.UtcNow - lastClipAt < TimeSpan.FromSeconds(3)) return;
        lastClipUrl = url;
        lastClipAt = DateTime.UtcNow;

        var site = LinkDetector.IsInstagram(url) ? "Instagram" : "X";
        if (settings.AutoDownloadFromClipboard)
        {
            jobs.AddUrl(url, settings.TranscribeByDefault);
            ShowBalloon($"{site} videosu indiriliyor", url, ToolTipIcon.Info, ShowMain);
        }
        else
        {
            var extra = settings.TranscribeByDefault ? " (transcript ile)" : "";
            ShowBalloon($"{site} linki algılandı", $"İndirmek{extra} için buraya tıklayın.\n{url}", ToolTipIcon.Info,
                () => { jobs.AddUrl(url, settings.TranscribeByDefault); ShowMain(); });
        }
    }

    void DownloadClipboard(bool transcribe)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        var urls = LinkDetector.ExtractAny(text);
        if (urls.Count == 0)
        {
            ShowBalloon("Panoda link yok", "Önce bir Instagram veya X linki kopyalayın.", ToolTipIcon.Warning, null);
            return;
        }
        foreach (var u in urls) jobs.AddUrl(u, transcribe);
        ShowMain();
    }

    void RunTool(string name, Func<Action<string>, CancellationToken, Task> action)
    {
        ShowMain();
        _ = Task.Run(async () =>
        {
            try
            {
                await action(jobs.WriteLog, CancellationToken.None);
                ui.Post(_ => { form.RefreshStatus(); ShowBalloon(name, "Tamamlandı.", ToolTipIcon.Info, null); }, null);
            }
            catch (Exception ex)
            {
                jobs.WriteLog($"{name}: HATA: {ex.Message}");
                ui.Post(_ => ShowBalloon(name, "Başarısız: " + Shorten(ex.Message, 180), ToolTipIcon.Error, ShowMain), null);
            }
        });
    }

    void ShowSettings()
    {
        ShowMain();
        using var dlg = new SettingsForm(settings);
        if (dlg.ShowDialog(form) == DialogResult.OK)
        {
            bridge.Restart();   // köprü açılıp kapatılmış olabilir
            form.ApplySettings();
            SyncMenu();
        }
    }

    void ShowMain()
    {
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    void ShowBalloon(string title, string text, ToolTipIcon icon, Action? onClick)
    {
        balloonAction = onClick;
        tray.ShowBalloonTip(5000, title, text, icon);
    }

    void SyncMenu() => watchItem.Checked = settings.WatchClipboard;

    void UpdateBusy()
    {
        var active = jobs.Jobs.Count(j => j.IsActive);
        tray.Icon = active > 0 ? busyIcon : idleIcon;
        tray.Text = active > 0 ? $"Kaptur — {active} iş sürüyor" : "Kaptur";
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    void Exit()
    {
        foreach (var j in jobs.Jobs.Where(j => j.IsActive)) jobs.Cancel(j);
        tray.Visible = false;
        bridge.Dispose();
        clipboard.Dispose();
        form.AllowClose = true;
        form.Close();
        ExitThread();
    }
}
