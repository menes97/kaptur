using System.Text.RegularExpressions;

namespace Kaptur;

static partial class LinkDetector
{
    // instagram.com/reel/…, /reels/…, /p/…, /tv/… ve yeni biçim instagram.com/<kullanıcı>/reel/…
    // x.com / twitter.com /<kullanıcı>/status/<id> ve fxtwitter, vxtwitter, fixupx gibi önizleme alan adları
    [GeneratedRegex(@"https?://(?:www\.|m\.)?(?:instagram\.com/(?:[\w.]+/)?(?:reels?|p|tv)/[\w-]+|(?:x|twitter|mobile\.twitter|fxtwitter|vxtwitter|fixupx|fixvx)\.com/\w+/status/\d+)[^\s""'<>]*",
        RegexOptions.IgnoreCase)]
    private static partial Regex SocialRx();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex AnyUrlRx();

    /// Panodaki metinden yalnızca Instagram/X video linklerini çıkarır (pano izleyici için).
    public static List<string> ExtractSocial(string text) =>
        SocialRx().Matches(text).Select(m => Normalize(m.Value)).Distinct().ToList();

    /// Elle yapıştırılan metindeki tüm http(s) linkleri; yt-dlp TikTok, YouTube vb. de destekler.
    public static List<string> ExtractAny(string text) =>
        AnyUrlRx().Matches(text).Select(m => Normalize(m.Value)).Distinct().ToList();

    public static bool IsInstagram(string url) => url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase);

    /// Paylaşım parametrelerini (?igsh=…, ?s=20) atar, önizleme alan adlarını x.com'a çevirir.
    static string Normalize(string url)
    {
        url = url.TrimEnd('.', ',', ')', ']', '!', '?');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var host = u.Host.ToLowerInvariant();
        bool social = host.EndsWith("instagram.com") || host is "x.com" or "twitter.com" or "mobile.twitter.com"
                      or "fxtwitter.com" or "vxtwitter.com" or "fixupx.com" or "fixvx.com";
        if (!social) return url;
        if (host is "fxtwitter.com" or "vxtwitter.com" or "fixupx.com" or "fixvx.com" or "twitter.com" or "mobile.twitter.com")
            host = "x.com";
        if (host == "m.instagram.com") host = "www.instagram.com";
        return $"https://{host}{u.AbsolutePath.TrimEnd('/')}";
    }
}
