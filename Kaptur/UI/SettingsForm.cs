namespace Kaptur;

sealed class SettingsForm : Form
{
    readonly AppSettings s;
    readonly TextBox outDir = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly ComboBox cookieMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly TextBox cookieFile = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right };
    readonly Button cookieBrowse = new() { Text = "Seç…", AutoSize = true };
    readonly CheckBox transcribeDefault = new() { Text = "Varsayılan olarak transcript de çıkar", AutoSize = true };
    readonly ComboBox model = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox language = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly ComboBox device = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly CheckBox watchClip = new() { Text = "Panoyu izle: Instagram/X linki kopyalanınca bildirim göster", AutoSize = true };
    readonly CheckBox autoDl = new() { Text = "Sormadan hemen indir", AutoSize = true, Margin = new Padding(20, 3, 3, 3) };
    readonly CheckBox autostart = new() { Text = "Windows ile birlikte tepside başlat", AutoSize = true };
    readonly CheckBox bridge = new() { AutoSize = true };

    static readonly (string Value, string Label)[] Models =
    [
        ("tiny", "tiny — en hızlı, düşük doğruluk"),
        ("base", "base"),
        ("small", "small — CPU için iyi denge"),
        ("medium", "medium"),
        ("large-v3-turbo", "large-v3-turbo — GPU için önerilen"),
        ("large-v3", "large-v3 — en doğru, en yavaş"),
    ];
    static readonly (string Value, string Label)[] Languages =
    [
        ("auto", "Otomatik algıla"), ("tr", "Türkçe"), ("en", "İngilizce"), ("de", "Almanca"),
        ("fr", "Fransızca"), ("es", "İspanyolca"), ("ar", "Arapça"), ("ru", "Rusça"),
    ];
    static readonly (string Value, string Label)[] Devices =
        [("auto", "Otomatik (varsa GPU)"), ("cuda", "NVIDIA GPU (CUDA)"), ("cpu", "CPU")];

    public SettingsForm(AppSettings settings)
    {
        s = settings;
        Text = "Kaptur — Ayarlar";
        Icon = AppIcon.Create();
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Padding = new Padding(14), Dock = DockStyle.Fill };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Header(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 4) };
            grid.Controls.Add(l);
            grid.SetColumnSpan(l, 3);
        }
        void Row(string label, Control c, Control? extra = null)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
            grid.Controls.Add(c);
            if (extra != null) grid.Controls.Add(extra); else grid.Controls.Add(new Label { AutoSize = true });
        }
        void Full(Control c)
        {
            grid.Controls.Add(c);
            grid.SetColumnSpan(c, 3);
        }
        Label Note(string text) => new()
        {
            Text = text, AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 4),
        };

        Header("İndirme");
        var outBrowse = new Button { Text = "Gözat…", AutoSize = true };
        outBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = outDir.Text, UseDescriptionForTitle = true, Description = "İndirme klasörü" };
            if (d.ShowDialog(this) == DialogResult.OK) outDir.Text = d.SelectedPath;
        };
        Row("Klasör:", outDir, outBrowse);
        cookieMode.Items.AddRange(["Yok (X'in herkese açık videoları için yeterli)", "Firefox'taki oturumu kullan", "cookies.txt dosyası"]);
        Row("Çerez kaynağı:", cookieMode);
        cookieBrowse.Click += (_, _) =>
        {
            using var d = new OpenFileDialog { Filter = "cookies.txt (Netscape)|*.txt|Tüm dosyalar|*.*", Title = "cookies.txt seç" };
            if (d.ShowDialog(this) == DialogResult.OK) cookieFile.Text = d.FileName;
        };
        Row("cookies.txt:", cookieFile, cookieBrowse);
        Full(Note("Instagram videoları çoğunlukla giriş ister. En kolayı Kaptur Chrome eklentisi: Chrome'daki oturumunuzu kendisi gönderir, " +
                  "burada bir şey seçmeniz gerekmez. Link yapıştırarak indirirken ise Chrome/Edge çerezleri şifreli olduğu için okunamaz: " +
                  "Firefox'ta giriş yapıp \"Firefox\" seçin ya da bir cookies.txt dosyası gösterin (hesabınıza erişim sağlar, paylaşmayın)."));

        Header("Tarayıcı eklentisi");
        bridge.Text = $"Chrome eklentisinin bağlanmasına izin ver (yalnızca bu bilgisayar, 127.0.0.1:{s.BridgePort})";
        Full(bridge);
        var setup = new Button { Text = "Eklenti kurulumu…", AutoSize = true };
        setup.Click += (_, _) => MainForm.ShowExtensionSetup(this);
        Full(setup);

        Header("Transcript (faster-whisper)");
        Full(transcribeDefault);
        model.Items.AddRange(Models.Select(m => (object)m.Label).ToArray());
        language.Items.AddRange(Languages.Select(m => (object)m.Label).ToArray());
        device.Items.AddRange(Devices.Select(m => (object)m.Label).ToArray());
        Row("Model:", model);
        Row("Dil:", language);
        Row("Cihaz:", device);
        Full(Note(Transcriber.HasNvidiaGpu
            ? "NVIDIA GPU bulundu. İlk transcriptte Python, faster-whisper ve CUDA kütüphaneleri (~1,5 GB) ile seçilen model indirilir."
            : "NVIDIA GPU bulunamadı; transcript CPU'da çalışır. CPU için \"small\" modeli önerilir."));

        Header("Genel");
        Full(watchClip);
        Full(autoDl);
        Full(autostart);

        var ok = new Button { Text = "Kaydet", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Vazgeç", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.AddRange([cancel, ok]);
        Full(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.Add(grid);

        outDir.Text = s.OutputDir;
        cookieMode.SelectedIndex = (int)s.Cookies;
        cookieFile.Text = s.CookiesFile;
        transcribeDefault.Checked = s.TranscribeByDefault;
        model.SelectedIndex = Math.Max(0, Array.FindIndex(Models, m => m.Value == s.WhisperModel));
        language.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, m => m.Value == s.WhisperLanguage));
        device.SelectedIndex = Math.Max(0, Array.FindIndex(Devices, m => m.Value == s.WhisperDevice));
        watchClip.Checked = s.WatchClipboard;
        autoDl.Checked = s.AutoDownloadFromClipboard;
        autostart.Checked = s.StartWithWindows;
        bridge.Checked = s.BridgeEnabled;

        cookieMode.SelectedIndexChanged += (_, _) => SyncEnabled();
        watchClip.CheckedChanged += (_, _) => SyncEnabled();
        SyncEnabled();
        ok.Click += (_, _) => Commit();
    }

    void SyncEnabled()
    {
        cookieFile.Enabled = cookieBrowse.Enabled = cookieMode.SelectedIndex == (int)CookieSource.File;
        autoDl.Enabled = watchClip.Checked;
    }

    void Commit()
    {
        s.OutputDir = string.IsNullOrWhiteSpace(outDir.Text) ? s.OutputDir : outDir.Text.Trim();
        s.Cookies = (CookieSource)cookieMode.SelectedIndex;
        s.CookiesFile = cookieFile.Text.Trim();
        s.TranscribeByDefault = transcribeDefault.Checked;
        s.WhisperModel = Models[model.SelectedIndex].Value;
        s.WhisperLanguage = Languages[language.SelectedIndex].Value;
        s.WhisperDevice = Devices[device.SelectedIndex].Value;
        s.WatchClipboard = watchClip.Checked;
        s.AutoDownloadFromClipboard = autoDl.Checked;
        s.StartWithWindows = autostart.Checked;
        s.BridgeEnabled = bridge.Checked;
        s.Save();
    }
}
