using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kaptur;

/// Chrome eklentisinin konuştuğu yerel HTTP köprüsü. mc-download-manager'daki LocalServer ile aynı fikir.
///
/// Yalnızca 127.0.0.1'de dinler ve şu istekleri reddeder:
///  - Host başlığı 127.0.0.1:port / localhost:port olmayanlar (DNS rebinding ile gelen sayfalar),
///  - Origin'i bizim eklentimiz olmayanlar (sıradan web sayfaları, "null" dahil),
///  - X-Kaptur-Extension başlığı olmayanlar. Bir web sayfası bu başlığı CORS ön kontrolü olmadan gönderemez,
///    ön kontrole de yalnızca eklenti kaynağına izin veririz. (Chrome, eklentinin GET isteklerinde Origin'i
///    her zaman göndermediği için yalnızca Origin'e güvenilmez.)
sealed class Bridge(JobManager jobs, AppSettings settings, SynchronizationContext ui, Action showWindow) : IDisposable
{
    public const string ExtensionHeader = "X-Kaptur-Extension";
    const int MaxHeaderBytes = 16 * 1024;
    const int MaxBodyBytes = 1024 * 1024;

    CancellationTokenSource cts = new();
    TcpListener? listener;

    public string Status { get; private set; } = "kapalı";
    int Port => settings.BridgePort;

    public void Start()
    {
        if (listener != null || !settings.BridgeEnabled) { if (!settings.BridgeEnabled) Status = "kapalı"; return; }
        cts = new CancellationTokenSource();
        try
        {
            var l = new TcpListener(IPAddress.Loopback, Port);
            l.Start();
            listener = l;
            Status = $"127.0.0.1:{Port}";
            _ = AcceptLoopAsync(l, cts.Token);
        }
        catch (SocketException ex)
        {
            Status = $"port {Port} kullanılamıyor";
            jobs.WriteLog($"Tarayıcı köprüsü başlatılamadı (port {Port}): {ex.Message}");
        }
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    void Stop()
    {
        cts.Cancel();
        listener?.Stop();
        listener = null;
        Status = "kapalı";
    }

    public void Dispose() => Stop();

    async Task AcceptLoopAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = Task.Run(() => ServeAsync(client, ct));
        }
    }

    sealed record Request(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    async Task ServeAsync(TcpClient client, CancellationToken appCt)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(appCt))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = client.GetStream();
                var req = await ReadRequestAsync(stream, timeout.Token);
                if (req == null) return;
                var (status, body, cors) = await HandleAsync(req);
                await WriteResponseAsync(stream, status, body, cors, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
    }

    async Task<(int, JsonNode?, string?)> HandleAsync(Request r)
    {
        var host = r.Headers.GetValueOrDefault("Host", "");
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}")
            return (403, Error("izin verilmeyen Host"), null);

        string? cors = null;
        if (r.Headers.TryGetValue("Origin", out var origin))
        {
            if (!ChromeIntegration.IsAllowedOrigin(origin, settings)) return (403, Error("izin verilmeyen kaynak"), null);
            cors = origin;
        }
        if (r.Method == "OPTIONS") return (204, null, cors);
        if (!r.Headers.ContainsKey(ExtensionHeader)) return (403, Error("yalnızca Kaptur eklentisi"), cors);

        try
        {
            return (r.Method, r.Path) switch
            {
                ("GET", "/ping") => (200, await OnUi(Ping), cors),
                ("GET", "/jobs") => (200, await OnUi(JobsJson), cors),
                ("POST", "/download") => (200, await DownloadAsync(ParseBody(r)), cors),
                ("POST", "/action") => (200, await ActionAsync(ParseBody(r)), cors),
                ("POST", "/show") => (200, await OnUi(() => { showWindow(); return Ok(); }), cors),
                _ => (404, Error("bulunamadı"), cors),
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return (400, Error(ex.Message), cors);
        }
    }

    JsonNode Ping() => new JsonObject
    {
        ["ok"] = true,
        ["app"] = "Kaptur",
        ["version"] = typeof(Bridge).Assembly.GetName().Version?.ToString(3),
        ["active"] = jobs.Jobs.Count(j => j.IsActive),
        ["transcriber"] = Transcriber.IsInstalled,
    };

    JsonNode JobsJson() => new JsonObject
    {
        ["items"] = new JsonArray(jobs.Jobs.Reverse().Take(15).Select(j => (JsonNode)new JsonObject
        {
            ["id"] = j.Id,
            ["title"] = j.Title,
            ["source"] = j.Source,
            ["site"] = j.SourceLabel,
            ["state"] = j.State.ToString().ToLowerInvariant(),
            ["active"] = j.IsActive,
            ["progress"] = j.Progress,
            ["status"] = j.Status,
            ["transcribe"] = j.Transcribe,
            ["hasFile"] = j.Files.Any(File.Exists),
            ["hasTranscript"] = j.Transcripts.Any(File.Exists),
        }).ToArray()),
    };

    async Task<JsonNode> DownloadAsync(JsonNode body)
    {
        var raw = body["url"]?.GetValue<string>() ?? throw new FormatException("url eksik");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("geçersiz url");
        var url = LinkDetector.ExtractAny(uri.AbsoluteUri).FirstOrDefault() ?? uri.AbsoluteUri;
        var transcribe = body["transcribe"]?.GetValue<bool>() ?? false;
        var cookieFile = WriteCookieFile(body["cookies"] as JsonArray);
        var options = new DownloadOptions(cookieFile, Text(body["userAgent"]), Text(body["referer"]));

        return await OnUi(() =>
        {
            if (jobs.Jobs.FirstOrDefault(j => j.IsActive && j.Source == url) is { } existing)
            {
                if (cookieFile != null) try { File.Delete(cookieFile); } catch { }
                return Ok(existing.Id, alreadyQueued: true);
            }
            var job = jobs.AddUrl(url, transcribe, options)!;
            jobs.WriteLog($"[#{job.Id}] Chrome eklentisinden geldi ({(cookieFile != null ? "tarayıcı çerezleriyle" : "çerezsiz")}).");
            return Ok(job.Id);
        });
    }

    Task<JsonNode> ActionAsync(JsonNode body)
    {
        var id = body["id"]?.GetValue<int>() ?? throw new FormatException("id eksik");
        var action = body["action"]?.GetValue<string>() ?? throw new FormatException("action eksik");
        return OnUi<JsonNode>(() =>
        {
            var job = jobs.Jobs.FirstOrDefault(j => j.Id == id) ?? throw new InvalidOperationException("iş bulunamadı");
            switch (action)
            {
                case "cancel": jobs.Cancel(job); break;
                case "retry": jobs.Retry(job); break;
                case "remove": jobs.Remove(job); break;
                case "transcribe": jobs.TranscribeExisting(job); break;
                // Yalnızca bu işin kendi dosyaları açılır; eklenti keyfi bir yol açtıramaz.
                case "open": MainForm.Shell(job.Files.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("dosya yok")); break;
                case "openTranscript": MainForm.Shell(job.Transcripts.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("transcript yok")); break;
                case "reveal": MainForm.Reveal(job.Files.FirstOrDefault(File.Exists)); break;
                default: throw new InvalidOperationException("bilinmeyen işlem");
            }
            return Ok(job.Id);
        });
    }

    /// chrome.cookies listesini yt-dlp'nin okuduğu Netscape cookies.txt biçimine yazar.
    static string? WriteCookieFile(JsonArray? cookies)
    {
        if (cookies == null || cookies.Count == 0) return null;
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        var count = 0;
        foreach (var c in cookies.OfType<JsonObject>())
        {
            string domain = Text(c["domain"]) ?? "", name = Text(c["name"]) ?? "", value = Text(c["value"]) ?? "",
                   path = Text(c["path"]) ?? "/";
            if (domain.Length == 0 || name.Length == 0) continue;
            // Satır tabanlı biçim: sekme ya da satır sonu içeren değerler dosyayı bozar, atla.
            if (new[] { domain, name, value, path }.Any(f => f.IndexOfAny(['\t', '\r', '\n']) >= 0)) continue;
            var secure = c["secure"]?.GetValue<bool>() ?? false;
            var expires = c["expirationDate"] is JsonValue ev && ev.TryGetValue<double>(out var d) ? (long)d : 0;
            // Python'un çerez okuyucusu, "alt alan adlarını kapsar" bayrağının alan adının noktayla başlamasıyla
            // aynı olmasını şart koşar.
            var includeSub = domain.StartsWith('.') ? "TRUE" : "FALSE";
            sb.Append($"{domain}\t{includeSub}\t{path}\t{(secure ? "TRUE" : "FALSE")}\t{expires}\t{name}\t{value}\n");
            count++;
        }
        if (count == 0) return null;
        var dir = Path.Combine(AppPaths.Root, "cookies");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"ext-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    static JsonNode ParseBody(Request r) =>
        JsonNode.Parse(r.Body) ?? throw new FormatException("boş gövde");

    static string? Text(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    static JsonObject Ok(int? id = null, bool alreadyQueued = false)
    {
        var o = new JsonObject { ["ok"] = true };
        if (id != null) o["id"] = id;
        if (alreadyQueued) o["alreadyQueued"] = true;
        return o;
    }

    static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    /// JobManager yalnızca UI iş parçacığında değiştirilir.
    Task<T> OnUi<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(_ =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, null);
        return tcs.Task;
    }

    static async Task<Request?> ReadRequestAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[MaxHeaderBytes];
        int len = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (len == buf.Length) return null;
            var n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) return null;
            len += n;
            headerEnd = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.Latin1.GetString(buf, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }

        var contentLength = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var c) ? c : 0;
        if (contentLength is < 0 or > MaxBodyBytes) return null;
        var body = new byte[contentLength];
        var have = Math.Min(len - headerEnd - 4, contentLength);
        Array.Copy(buf, headerEnd + 4, body, 0, have);
        while (have < contentLength)
        {
            var n = await s.ReadAsync(body.AsMemory(have), ct);
            if (n == 0) return null;
            have += n;
        }

        var target = first[1];
        var q = target.IndexOf('?');
        return new Request(first[0].ToUpperInvariant(), q < 0 ? target : target[..q], headers, body);
    }

    static async Task WriteResponseAsync(NetworkStream s, int status, JsonNode? body, string? cors, CancellationToken ct)
    {
        var payload = body == null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString());
        var reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        var sb = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
        if (payload.Length > 0) sb.Append("Content-Type: application/json; charset=utf-8\r\n");
        sb.Append($"Content-Length: {payload.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n");
        if (cors != null)
        {
            sb.Append($"Access-Control-Allow-Origin: {cors}\r\nVary: Origin\r\n");
            sb.Append($"Access-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, {ExtensionHeader}\r\n");
            // Chrome'un yerel ağ erişimi (Private/Local Network Access) ön kontrolü için.
            sb.Append("Access-Control-Allow-Private-Network: true\r\nAccess-Control-Allow-Local-Network: true\r\n");
        }
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
        if (payload.Length > 0) await s.WriteAsync(payload, ct);
    }
}
