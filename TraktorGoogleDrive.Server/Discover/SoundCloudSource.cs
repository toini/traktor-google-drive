using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>A SoundCloud track that its official oEmbed endpoint confirmed exists and is embeddable.</summary>
/// <param name="BandcampUrls">Bandcamp pages the uploader put in the track's description (a buy link, usually).</param>
public sealed record SoundCloudTrack(string Url, string Title, string? Author, long? TrackId, IReadOnlyList<string> BandcampUrls);

/// <summary>
/// SoundCloud track page URLs: <c>https://soundcloud.com/&lt;user&gt;/&lt;track&gt;</c>,
/// rebuilt from its two parts, so nothing a caller sends steers a request
/// anywhere else (ADR 20261025-54 §9).
/// </summary>
public static partial class SoundCloudUrls
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]{0,99}$")]
    private static partial Regex Part();

    private static readonly HashSet<string> ReservedUsers = new(StringComparer.Ordinal)
    {
        "discover", "search", "you", "stream", "upload", "charts", "pages", "settings", "messages", "notifications",
        "feed", "mobile", "terms-of-use", "jobs", "pro", "oembed", "tags", "people", "signin", "logout", "imprint",
    };

    private static readonly HashSet<string> ReservedTracks = new(StringComparer.Ordinal)
    {
        "sets", "tracks", "likes", "reposts", "albums", "popular-tracks", "following", "followers", "comments", "spotlight",
    };

    public static string? TrackPage(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        if (uri.Host.ToLowerInvariant() is not ("soundcloud.com" or "www.soundcloud.com" or "m.soundcloud.com")) return null;
        var parts = uri.AbsolutePath.ToLowerInvariant().Trim('/').Split('/');
        if (parts.Length != 2 || !Part().IsMatch(parts[0]) || !Part().IsMatch(parts[1])) return null;
        if (ReservedUsers.Contains(parts[0]) || ReservedTracks.Contains(parts[1])) return null;
        return $"https://soundcloud.com/{parts[0]}/{parts[1]}";
    }

    public static Uri OEmbed(string trackPage) =>
        new($"https://soundcloud.com/oembed?format=json&url={Uri.EscapeDataString(trackPage)}");

    /// <summary>SoundCloud's permalink style: lower case, accents folded, apostrophes dropped, the rest hyphens.</summary>
    public static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Replace("'", "").Replace("’", "").Replace("&", " ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) && ch < 128 ? char.ToLowerInvariant(ch) : '-');
        }
        return Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
    }

    /// <summary>
    /// At most <paramref name="max"/> permalinks to check, best first: the
    /// uploader shown under the title (each name of "Tengri, Jahbo"; the part
    /// before a bracket of "Dhatri (Root Sector)"), then the first artist, with
    /// the title's slug.
    /// </summary>
    public static IReadOnlyList<string> Guesses(string artist, string title, string? uploader, int max = 3)
    {
        var track = Slug(Regex.Replace(title, @"\((?:original mix|truncated)\)", "", RegexOptions.IgnoreCase));
        if (track.Length == 0 || title.Contains('…') || title.Contains("(truncated)", StringComparison.OrdinalIgnoreCase)) return [];
        var users = new List<string>();
        foreach (var name in Regex.Split(uploader ?? "", @"\s*,\s*"))
            users.Add(Slug(Regex.Replace(name, @"\(.*$", "")));
        users.Add(Slug(DiscogsSource.FirstArtist(artist)));
        return users.Where(u => u.Length > 0).Distinct()
            .Select(u => TrackPage($"https://soundcloud.com/{u}/{track}"))
            .OfType<string>().Distinct().Take(max).ToList();
    }
}

/// <summary>
/// SoundCloud as a fallback player (ADR 20261025-54, "Crates"). Only the official
/// oEmbed endpoint is called, only on Toni's action for one item, at most a few
/// permalink guesses at a time, never a search and never a page scrape. The
/// widget that plays it (<c>w.soundcloud.com/player</c>) runs in his browser.
/// </summary>
public sealed partial class SoundCloudSource
{
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);

    [GeneratedRegex(@"tracks(?:%2F|/)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TrackIdInHtml();

    private readonly PoliteFetcher _fetcher;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (SoundCloudTrack? Track, DateTimeOffset At)> _cache = new();

    public SoundCloudSource(PoliteFetcher fetcher, TimeProvider clock)
    {
        _fetcher = fetcher;
        _clock = clock;
    }

    public PoliteFetcher Fetcher => _fetcher;

    /// <summary>The track at a canonical page URL, or null when SoundCloud has no such (public, embeddable) track.</summary>
    public async Task<SoundCloudTrack?> CheckAsync(string trackPage, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(trackPage, out var hit) && now - hit.At < Ttl) return hit.Track;
        SoundCloudTrack? track;
        try { track = ParseOEmbed(await _fetcher.GetStringAsync(SoundCloudUrls.OEmbed(trackPage), ct), trackPage); }
        catch (FetchFailedException e) when (e.Status is 404 or 403 or 401) { track = null; }
        if (_cache.Count > 2000) _cache.Clear();
        _cache[trackPage] = (track, now);
        return track;
    }

    public async Task<SoundCloudTrack?> GuessAsync(string artist, string title, string? uploader, CancellationToken ct)
    {
        foreach (var page in SoundCloudUrls.Guesses(artist, title, uploader))
            if (await CheckAsync(page, ct) is { } found) return found;
        return null;
    }

    /// <summary>A track's oEmbed answer; a playlist or a profile is not a track.</summary>
    public static SoundCloudTrack? ParseOEmbed(string json, string trackPage)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var html = r.TryGetProperty("html", out var h) ? h.GetString() ?? "" : "";
        if (html.Contains("playlists", StringComparison.OrdinalIgnoreCase) || html.Contains("users%2F", StringComparison.OrdinalIgnoreCase))
            return null;
        var m = TrackIdInHtml().Match(html);
        var author = r.TryGetProperty("author_name", out var a) ? a.GetString() : null;
        // oEmbed titles read "Something Different by Codex Spiralis".
        var title = r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (author is not null && title.EndsWith(" by " + author, StringComparison.Ordinal)) title = title[..^(" by " + author).Length];
        var description = r.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        return new SoundCloudTrack(trackPage, title, author,
            m.Success && long.TryParse(m.Groups[1].Value, out var id) ? id : null,
            DiscogsParser.BandcampPages(BandcampInText().Matches(description).Select(x => x.Value)));
    }

    [GeneratedRegex(@"https?://[a-z0-9-]+\.bandcamp\.com[^\s""'<>\]\)]*", RegexOptions.IgnoreCase)]
    private static partial Regex BandcampInText();
}
