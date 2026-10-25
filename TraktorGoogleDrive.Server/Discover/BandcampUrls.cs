using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>
/// The only Bandcamp URLs the server will fetch (ADR 20261025-54 §9). Anything a
/// caller sends is parsed into one of these shapes and the URL is rebuilt from the
/// parts, so a request can never steer the fetch anywhere else: no other host, no
/// <c>/search</c>, no <c>/api/</c>, no query string.
/// </summary>
public static partial class BandcampUrls
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex Subdomain();

    [GeneratedRegex(@"^/(?<kind>album|track)/(?<slug>[a-z0-9][a-z0-9-]{0,200})/?$")]
    private static partial Regex ReleasePath();

    // bandcamp.com's own pages, and subdomains that are not artist or label pages.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "www", "bandcamp", "daily", "blog", "get", "f4", "t4", "s4", "api",
    };

    /// <summary>A label's or artist's <c>/music</c> page, from any URL on that subdomain (or the bare subdomain).</summary>
    public static Uri? MusicPage(string? input)
    {
        var host = HostOf(input);
        return host is null ? null : new Uri($"https://{host}/music");
    }

    /// <summary>A release or track page, canonical, or null if the input is not one.</summary>
    public static Uri? ReleasePage(string? input)
    {
        if (!TryParse(input, out var uri)) return null;
        var host = HostOf(uri.Host);
        if (host is null) return null;
        var m = ReleasePath().Match(uri.AbsolutePath.ToLowerInvariant());
        return m.Success ? new Uri($"https://{host}/{m.Groups["kind"].Value}/{m.Groups["slug"].Value}") : null;
    }

    /// <summary>The stream CDN. A redirect goes nowhere else.</summary>
    public static bool IsStreamUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && u.Host.EndsWith(".bcbits.com", StringComparison.OrdinalIgnoreCase);

    private static string? HostOf(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        string host;
        if (TryParse(text, out var uri)) host = uri.Host;
        else if (!text.Contains('/') && !text.Contains(':')) host = text.Contains('.') ? text : text + ".bandcamp.com";
        else return null;

        host = host.ToLowerInvariant();
        if (!host.EndsWith(".bandcamp.com")) return null;
        var sub = host[..^".bandcamp.com".Length];
        if (!Subdomain().IsMatch(sub) || Reserved.Contains(sub)) return null;
        return host;
    }

    private static bool TryParse(string? input, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var text = input.Trim();
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!text.Contains('/')) return false;
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp) return false;
        if (!parsed.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }
}
