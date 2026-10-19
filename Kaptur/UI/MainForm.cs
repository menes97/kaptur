using System.Diagnostics;

namespace Kaptur;

sealed class MainForm : Form
{
    readonly AppSettings settings;
    readonly JobManager jobs;
    readonly Dictionary<Job, ListViewItem> items = [];

    readonly TextBox urlBox = new()
    {
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        PlaceholderText = "https://www.instagram.com/reel/…   veya   https://x.com/…/status/…",
    };
    readonly Button addBtn = new() { Text = "İndir", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 2, 10, 2) };
    readonly CheckBox transcribeChk = new() { Text = "Transcript de çıkar", AutoSize = true, Margin = new Padding(0, 6, 16, 0) };
    readonly ListView list = new()
    {
        View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, HideSelection = false,
        GridLines = false, ShowItemToolTips = true,
    };
    readonly TextBox logBox = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 8.5f), BackColor = SystemColors.Window, WordWrap = false,
    };
    readonly Label statusLbl = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) };

    public event Action? SettingsRequested;
    public event Action? InstallTranscriberRequested;
    public event Action? UpdateYtDlpRequested;
    public bool AllowClose;
    /// Tarayıcı köprüsünün durumu ("127.0.0.1:48214" ya da hata); TrayContext atar.
    public Func<string>? BridgeStatus;

    public MainForm(AppSettings settings, JobManager jobs)
    {
        this.settings = settings;
        this.jobs = jobs;

        Text = "Kaptur — Instagram & X indirici";
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(900, 560);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;
        AcceptButton = addBtn;

        transcribeChk.Checked = settings.TranscribeByDefault;

        list.Columns.Add("Kaynak", 80);
        list.Columns.Add("Başlık / link", 340);
        list.Columns.Add("Durum", 260);
        list.Columns.Add("İlerleme", 150);
        list.ContextMenuStrip = BuildItemMenu();
        list.DoubleClick += (_, _) => { if (Selected is { } j) OpenPrimary(j); };
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) foreach (var j in SelectedJobs) jobs.Remove(j); };

        var settingsBtn = new Button { Text = "Ayarlar…", AutoSize = true };
        settingsBtn.Click += (_, _) => SettingsRequested?.Invoke();
        var folderBtn = new Button { Text = "İndirme klasörü", AutoSize = true };
        folderBtn.Click += (_, _) => OpenFolder(settings.OutputDir);
        var toolsMenu = new ContextMenuStrip();
        toolsMenu.Items.Add("Chrome eklentisini kur…", null, (_, _) => ShowExtensionSetup(this));
        toolsMenu.Items.Add(new ToolStripSeparator());
        toolsMenu.Items.Add("Transcript bileşenlerini şimdi kur", null, (_, _) => InstallTranscriberRequested?.Invoke());
        toolsMenu.Items.Add("yt-dlp'yi güncelle", null, (_, _) => UpdateYtDlpRequested?.Invoke());
        toolsMenu.Items.Add(new ToolStripSeparator());
        toolsMenu.Items.Add("Araç klasörünü aç", null, (_, _) => OpenFolder(AppPaths.Tools));
        toolsMenu.Items.Add("Günlük dosyasını aç", null, (_, _) => { if (File.Exists(AppPaths.LogFile)) Shell(AppPaths.LogFile); });
        var toolsBtn = new Button { Text = "Araçlar ▾", AutoSize = true };
        toolsBtn.Click += (_, _) => toolsMenu.Show(toolsBtn, new Point(0, toolsBtn.Height));

        var hint = new Label
        {
            AutoSize = true,
            Text = "Instagram / X linkini yapıştırın (Ctrl+V) ya da bir video dosyasını pencereye sürükleyip yalnızca transcript çıkarın.",
            Margin = new Padding(0, 0, 0, 6),
        };

        var inputRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.Controls.Add(urlBox, 0, 0);
        inputRow.Controls.Add(addBtn, 1, 0);

        var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 6) };
        options.Controls.AddRange([transcribeChk, settingsBtn, folderBtn, toolsBtn]);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(hint, 0, 0);
        root.Controls.Add(inputRow, 0, 1);
        root.Controls.Add(options, 0, 2);
        root.Controls.Add(list, 0, 3);
        root.Controls.Add(logBox, 0, 4);
        root.Controls.Add(statusLbl, 0, 5);
        Controls.Add(root);

        addBtn.Click += (_, _) => SubmitText(urlBox.Text, fromBox: true);
        KeyDown += OnKeyDown;
        DragEnter += (_, e) => e.Effect = e.Data != null && (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += OnDragDrop;
        Resize += (_, _) => FitColumns();

        jobs.JobAdded += AddItem;
        jobs.JobChanged += RefreshItem;
        jobs.JobRemoved += j => { if (items.Remove(j, out var it)) list.Items.Remove(it); };
        jobs.Log += AppendLog;
        foreach (var j in jobs.Jobs) AddItem(j);
        RefreshStatus();
    }

    public void ApplySettings()
    {
        transcribeChk.Checked = settings.TranscribeByDefault;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        var yt = ToolManager.FindYtDlp() != null ? "hazır" : "ilk indirmede kurulacak";
        var ff = ToolManager.FindFfmpeg() != null ? "hazır" : "ilk indirmede kurulacak";
        var tr = Transcriber.IsInstalled ? "kurulu" : "ilk kullanımda kurulacak";
        var gpu = Transcriber.HasNvidiaGpu ? "NVIDIA GPU" : "CPU";
        var cookies = settings.Cookies switch
        {
            CookieSource.Firefox => "Firefox",
            CookieSource.File => File.Exists(settings.CookiesFile) ? "cookies.txt" : "cookies.txt (dosya yok!)",
            _ => "yok",
        };
        var bridge = BridgeStatus?.Invoke() ?? "kapalı";
        statusLbl.Text = $"yt-dlp: {yt} · ffmpeg: {ff} · transcript: {tr} ({gpu}, {settings.WhisperModel}) · çerez: {cookies} · eklenti köprüsü: {bridge}";
    }

    /// Paketlenmemiş eklenti kurulumunu adım adım anlatır, chrome://extensions adresini panoya koyar ve klasörü açar.
    internal static void ShowExtensionSetup(IWin32Window? owner)
    {
        try { Clipboard.SetText("chrome://extensions"); } catch { }
        var r = MessageBox.Show(owner, ChromeIntegration.SetupInstructions, "Kaptur — Chrome eklentisi",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
        if (r == DialogResult.OK) OpenFolder(ChromeIntegration.ExtensionDir);
    }

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Metin kutusu dışındayken Ctrl+V: panodaki link(ler)i doğrudan kuyruğa ekle.
        if (e.Control && e.KeyCode == Keys.V && ActiveControl is not TextBox && Clipboard.ContainsText())
        {
            SubmitText(Clipboard.GetText(), fromBox: false);
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var p in paths.Where(File.Exists))
            {
                if (Transcriber.IsMedia(p)) jobs.AddLocalFile(p);
                else AppendLog($"Desteklenmeyen dosya türü: {Path.GetFileName(p)}");
            }
        }
        else if (e.Data?.GetData(DataFormats.Text) is string text)
            SubmitText(text, fromBox: false);
    }

    void SubmitText(string text, bool fromBox)
    {
        var urls = LinkDetector.ExtractAny(text);
        if (urls.Count == 0)
        {
            if (fromBox) MessageBox.Show(this, "Geçerli bir http(s) linki bulunamadı.", "Kaptur", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (var u in urls) jobs.AddUrl(u, transcribeChk.Checked);
        if (fromBox) urlBox.Clear();
    }

    void AddItem(Job job)
    {
        var it = new ListViewItem(["", "", "", ""]) { Tag = job };
        items[job] = it;
        list.Items.Insert(0, it);
        RefreshItem(job);
    }

    void RefreshItem(Job job)
    {
        if (!items.TryGetValue(job, out var it)) return;
        it.SubItems[0].Text = job.SourceLabel;
        it.SubItems[1].Text = job.Title.Length > 0 ? job.Title.ReplaceLineEndings(" ") : job.Source;
        it.SubItems[2].Text = job.Status;
        it.SubItems[3].Text = job.State switch
        {
            JobState.Done => job.Transcribe ? "✔ video + metin" : "✔",
            JobState.Failed => "✖",
            JobState.Canceled => "—",
            _ => Bar(job.Progress),
        };
        it.ToolTipText = job.Source + (job.State == JobState.Failed ? "\n" + job.Status : "");
        it.ForeColor = job.State switch
        {
            JobState.Failed => Color.Firebrick,
            JobState.Canceled => SystemColors.GrayText,
            JobState.Done => Color.FromArgb(0, 110, 60),
            _ => SystemColors.WindowText,
        };
        if (job.State is JobState.Done or JobState.Failed) RefreshStatus();
    }

    static string Bar(double p)
    {
        if (p < 0) return "⋯";
        var n = (int)Math.Round(p * 10);
        return new string('█', n) + new string('░', 10 - n) + $" %{p * 100:0}";
    }

    void AppendLog(string line)
    {
        if (logBox.TextLength > 60_000) logBox.Text = logBox.Text[^30_000..];
        logBox.AppendText(line + Environment.NewLine);
    }

    void FitColumns()
    {
        var w = list.ClientSize.Width - list.Columns[0].Width - list.Columns[2].Width - list.Columns[3].Width - 4;
        if (w > 150) list.Columns[1].Width = w;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitColumns();
        urlBox.Focus();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Pencereyi kapatmak uygulamayı kapatmaz; tepside çalışmaya devam eder.
        if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    Job? Selected => list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as Job : null;
    IEnumerable<Job> SelectedJobs => list.SelectedItems.Cast<ListViewItem>().Select(i => (Job)i.Tag!).ToList();

    ContextMenuStrip BuildItemMenu()
    {
        var m = new ContextMenuStrip();
        var openVideo = new ToolStripMenuItem("Videoyu aç", null, (_, _) => { if (Selected is { } j && j.Files.Count > 0) Shell(j.Files[0]); });
        var openText = new ToolStripMenuItem("Transcripti aç", null, (_, _) => { if (Selected is { } j && j.Transcripts.Count > 0) Shell(j.Transcripts[0]); });
        var reveal = new ToolStripMenuItem("Klasörde göster", null, (_, _) => { if (Selected is { } j) Reveal(j.Files.FirstOrDefault()); });
        var transcribe = new ToolStripMenuItem("Transcript çıkar", null, (_, _) => { foreach (var j in SelectedJobs) jobs.TranscribeExisting(j); });
        var retry = new ToolStripMenuItem("Yeniden dene", null, (_, _) => { foreach (var j in SelectedJobs) jobs.Retry(j); });
        var copy = new ToolStripMenuItem("Linki kopyala", null, (_, _) => { if (Selected is { } j) Clipboard.SetText(j.Source); });
        var cancel = new ToolStripMenuItem("İptal", null, (_, _) => { foreach (var j in SelectedJobs) jobs.Cancel(j); });
        var remove = new ToolStripMenuItem("Listeden kaldır", null, (_, _) => { foreach (var j in SelectedJobs) jobs.Remove(j); });
        m.Items.AddRange([openVideo, openText, reveal, new ToolStripSeparator(), transcribe, retry, copy, new ToolStripSeparator(), cancel, remove]);
        m.Opening += (_, e) =>
        {
            if (Selected is not { } j) { e.Cancel = true; return; }
            openVideo.Enabled = reveal.Enabled = j.Files.Count > 0 && File.Exists(j.Files[0]);
            openText.Enabled = j.Transcripts.Count > 0;
            transcribe.Enabled = !j.IsActive && j.Files.Any(Transcriber.IsMedia);
            retry.Enabled = j.State is JobState.Failed or JobState.Canceled;
            cancel.Enabled = j.IsActive;
            copy.Enabled = !j.IsLocalFile;
        };
        return m;
    }

    void OpenPrimary(Job j)
    {
        if (j.Transcripts.Count > 0 && File.Exists(j.Transcripts[0]) && j.Files.Count == 0) Shell(j.Transcripts[0]);
        else if (j.Files.Count > 0 && File.Exists(j.Files[0])) Shell(j.Files[0]);
    }

    internal static void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    internal static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Shell(dir);
    }

    internal static void Reveal(string? file)
    {
        if (file == null || !File.Exists(file)) return;
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }
}
