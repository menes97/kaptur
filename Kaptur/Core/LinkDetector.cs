using System.Text.RegularExpressions;
using System.Web;

namespace Kaptur;

static partial class LinkDetector
{
    // instagram.com/reel/…, /reels/…, /p/…, /tv/… ve yeni biçim instagram.com/<kullanıcı>/reel/…
    // x.com / twitter.com /<kullanıcı>/status/<id> ve fxtwitter, vxtwitter, fixupx gibi önizleme alan adları
    // youtube.com/watch?v=…, /shorts/…, youtu.be/…
    [GeneratedRegex(@"https?://(?:www\.|m\.)?(?:instagram\.com/(?:[\w.]+/)?(?:reels?|p|tv)/[\w-]+|(?:x|twitter|mobile\.twitter|fxtwitter|vxtwitter|fixupx|fixvx)\.com/\w+/status/\d+|(?:music\.)?youtube\.com/(?:watch\?[^\s""'<>]*v=|shorts/)[\w-]{6,}|youtu\.be/[\w-]{6,})[^\s""'<>]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex SocialRx();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex AnyUrlRx();

    /// Panodaki metinden yalnızca Instagram/X/YouTube video linklerini çıkarır (pano izleyici için).
    public static List<string> ExtractSocial(string text) =>
        SocialRx().Matches(text).Select(m => Normalize(m.Value)).Distinct().ToList();

    /// Elle yapıştırılan metindeki tüm http(s) linkleri; yt-dlp TikTok vb. başka siteleri de destekler.
    public static List<string> ExtractAny(string text) =>
        AnyUrlRx().Matches(text).Select(m => Normalize(m.Value)).Distinct().ToList();

    public static bool IsInstagram(string url) => url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase);

    public static bool IsYouTube(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
                                                            || u.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase));

    /// Kullanıcıya gösterilecek kaynak adı.
    public static string SiteName(string url) =>
        IsInstagram(url) ? "Instagram" : IsYouTube(url) ? "YouTube"
        : url.Contains("x.com/", StringComparison.OrdinalIgnoreCase) ? "X" : "Web";

    /// Paylaşım parametrelerini (?igsh=…, ?s=20, ?si=…) atar, önizleme alan adlarını x.com'a çevirir.
    /// YouTube'da videonun kimliği olan v= parametresi korunur.
    static string Normalize(string url)
    {
        url = url.TrimEnd('.', ',', ')', ']', '!', '?');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var host = u.Host.ToLowerInvariant();
        if (host.EndsWith("youtube.com") || host == "youtu.be")
        {
            if (host == "youtu.be") return $"https://youtu.be{u.AbsolutePath.TrimEnd('/')}";
            if (u.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase)
                && HttpUtility.ParseQueryString(u.Query)["v"] is { Length: > 0 } id)
                return $"https://www.youtube.com/watch?v={id}";
            if (u.AbsolutePath.StartsWith("/shorts/", StringComparison.OrdinalIgnoreCase))
                return $"https://www.youtube.com{u.AbsolutePath.TrimEnd('/')}";
            return url;
        }
        bool social = host.EndsWith("instagram.com") || host is "x.com" or "twitter.com" or "mobile.twitter.com"
                      or "fxtwitter.com" or "vxtwitter.com" or "fixupx.com" or "fixvx.com";
        if (!social) return url;
        if (host is "fxtwitter.com" or "vxtwitter.com" or "fixupx.com" or "fixvx.com" or "twitter.com" or "mobile.twitter.com")
            host = "x.com";
        if (host == "m.instagram.com") host = "www.instagram.com";
        return $"https://{host}{u.AbsolutePath.TrimEnd('/')}";
    }
}
