using System.Diagnostics;
using System.Text;

namespace Kaptur;

static class ProcessRunner
{
    /// Konsol penceresi açmadan bir araç çalıştırır; stdout/stderr satırlarını geldikçe iletir.
    /// İptal edilirse süreç ağacını öldürür.
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string, bool> onLine,
        CancellationToken ct, IDictionary<string, string>? env = null, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        // yt-dlp.exe ve Python, Windows'ta varsayılan olarak konsol kod sayfasıyla yazar; Türkçe karakterler bozulmasın.
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data, false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data, true); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }
}
