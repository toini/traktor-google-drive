using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>A release as listed on a label's or artist's <c>/music</c> page. No date: the page has none.</summary>
public sealed record BandcampReleaseRef(string Url, string Type, long? Id, string Title, string? Artist);

/// <summary>One track of a release page.</summary>
public sealed record BandcampTrack(
    long TrackId,
    int? Number,
    string Title,
    string Artist,
    double Duration,
    string? TrackUrl,
    string? StreamUrl,
    DateTimeOffset? StreamExpires,
    string? Isrc);

/// <summary>What a release (or single-track) page says about itself.</summary>
public sealed record BandcampRelease(
    string Url,
    string Type,
    long Id,
    string Title,
    string Artist,
    string? Label,
    string? LabelUrl,
    DateTimeOffset? ReleaseDate,
    IReadOnlyList<string> Tags,
    IReadOnlyList<BandcampTrack> Tracks,
    string? AlbumUrl,
    int? StreamingLimit);

/// <summary>
/// Reads Bandcamp's HTML (ADR 20261025-54 §1.2, spike S1). Pure functions over a
/// page's text, so every case is tested against recorded pages and none needs the
/// network. Bandcamp changes its markup without notice; a page this cannot read
/// gives an empty list or null, never an exception.
/// </summary>
public static class BandcampParser
{
    private static readonly Regex GridItem = new(
        @"<li\s+data-item-id=""(?<type>album|track)-(?<id>\d+)""[^>]*>\s*<a\s+href=""(?<href>[^""]+)""[\s\S]*?<p class=""title"">(?<title>[\s\S]*?)</p>",
        RegexOptions.Compiled);

    private static readonly Regex ArtistOverride = new(
        @"<span class=""artist-override"">(?<artist>[\s\S]*?)</span>", RegexOptions.Compiled);

    private static readonly Regex ClientItems = new(@"data-client-items=""(?<json>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Tralbum = new(@"data-tralbum=""(?<json>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Band = new(@"data-band=""(?<json>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex LdJson = new(
        @"<script type=""application/ld\+json"">(?<json>[\s\S]*?)</script>", RegexOptions.Compiled);
    private static readonly Regex Tags = new(@"<[^>]+>", RegexOptions.Compiled);

    // "01. obwigszyh - 1616 (Original Mix)": labels often put the number and the
    // artist into the title and leave the track's artist empty.
    private static readonly Regex NumberPrefix = new(@"^\s*\d{1,3}\s*[.)\-]\s*", RegexOptions.Compiled);

    /// <summary>The releases on a <c>/music</c> page, in page order (newest first, as Bandcamp shows them).</summary>
    public static IReadOnlyList<BandcampReleaseRef> ParseMusicPage(string html, Uri pageUrl)
    {
        var result = new List<BandcampReleaseRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in GridItem.Matches(html))
        {
            var url = Absolute(pageUrl, WebUtility.HtmlDecode(m.Groups["href"].Value));
            if (url is null || !seen.Add(url)) continue;

            var titleHtml = m.Groups["title"].Value;
            string? artist = null;
            var over = ArtistOverride.Match(titleHtml);
            if (over.Success)
            {
                artist = Clean(over.Groups["artist"].Value);
                titleHtml = titleHtml[..over.Index];
            }
            var title = Clean(Tags.Replace(titleHtml, " "));
            result.Add(new BandcampReleaseRef(url, m.Groups["type"].Value,
                long.TryParse(m.Groups["id"].Value, out var id) ? id : null, title, artist));
        }

        // Everything after the first rows is lazy-loaded from this attribute.
        var client = ClientItems.Match(html);
        if (client.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(client.Groups["json"].Value));
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var url = Absolute(pageUrl, Str(item, "page_url"));
                    if (url is null || !seen.Add(url)) continue;
                    result.Add(new BandcampReleaseRef(
                        url,
                        Str(item, "type") ?? (url.Contains("/track/") ? "track" : "album"),
                        Long(item, "id"),
                        Str(item, "title") ?? "",
                        Str(item, "artist")));
                }
            }
            catch (JsonException)
            {
                // A changed attribute format loses the older releases, not the page.
            }
        }

        return result;
    }

    /// <summary>A release or track page, or null when the page carries no release data.</summary>
    public static BandcampRelease? ParseRelease(string html, Uri pageUrl)
    {
        var tralbum = Tralbum.Match(html);
        if (!tralbum.Success) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(WebUtility.HtmlDecode(tralbum.Groups["json"].Value)); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            var current = root.TryGetProperty("current", out var c) ? c : default;

            var type = Str(root, "item_type") ?? (pageUrl.AbsolutePath.StartsWith("/track/") ? "track" : "album");
            var releaseArtist = Clean(Str(root, "artist") ?? "");
            var title = Clean(current.ValueKind == JsonValueKind.Object ? Str(current, "title") ?? "" : "");
            var id = Long(root, "id") ?? 0;

            // A track page reports its album's date; an album page has both.
            var date = ParseDate(Str(root, "album_release_date"))
                       ?? (current.ValueKind == JsonValueKind.Object
                           ? ParseDate(Str(current, "release_date")) ?? ParseDate(Str(current, "publish_date"))
                           : null);

            var albumUrl = Absolute(pageUrl, Str(root, "album_url"));
            int? limit = root.TryGetProperty("play_cap_data", out var cap) && cap.ValueKind == JsonValueKind.Object
                ? (int?)Long(cap, "streaming_limit")
                : null;

            string? isrc = current.ValueKind == JsonValueKind.Object ? Str(current, "isrc") : null;

            var tracks = new List<BandcampTrack>();
            if (root.TryGetProperty("trackinfo", out var infos) && infos.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in infos.EnumerateArray())
                {
                    var trackId = Long(t, "track_id") ?? Long(t, "id");
                    if (trackId is null) continue;

                    var (trackTitle, trackArtist) = SplitTitle(Str(t, "title") ?? "", Str(t, "artist"), releaseArtist);
                    string? stream = null;
                    if (t.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
                        stream = Str(file, "mp3-128");

                    tracks.Add(new BandcampTrack(
                        trackId.Value,
                        (int?)Long(t, "track_num"),
                        trackTitle,
                        trackArtist,
                        t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
                        Absolute(pageUrl, Str(t, "title_link")) ?? (type == "track" ? pageUrl.GetLeftPart(UriPartial.Path) : null),
                        stream,
                        StreamExpiry(stream),
                        type == "track" ? isrc : null));
                }
            }

            var (label, labelUrl) = ReadLabel(html);
            var tags = ReadKeywords(html);

            return new BandcampRelease(
                pageUrl.GetLeftPart(UriPartial.Path),
                type,
                id,
                title,
                releaseArtist,
                label,
                labelUrl,
                date,
                tags,
                tracks,
                type == "track" ? albumUrl : null,
                limit);
        }
    }

    /// <summary>
    /// Stream URLs end in <c>token=&lt;unix expiry&gt;_&lt;signature&gt;</c>; the expiry is
    /// fetch time + 24 h (spike S1).
    /// </summary>
    public static DateTimeOffset? StreamExpiry(string? streamUrl)
    {
        if (streamUrl is null) return null;
        var m = Regex.Match(streamUrl, @"[?&]token=(\d{9,11})_");
        return m.Success && long.TryParse(m.Groups[1].Value, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
    }

    public static (string Title, string Artist) SplitTitle(string rawTitle, string? trackArtist, string releaseArtist)
    {
        var title = Clean(rawTitle);
        var artist = Clean(trackArtist ?? "");
        if (artist.Length > 0) return (title, artist);

        // Only strip a leading number when an "artist - title" follows, so a track
        // genuinely called "1616" keeps its name.
        var withoutNumber = NumberPrefix.Replace(title, "");
        var dash = withoutNumber.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
            return (withoutNumber[(dash + 3)..].Trim(), withoutNumber[..dash].Trim());

        return (title, releaseArtist);
    }

    private static (string? Name, string? Url) ReadLabel(string html)
    {
        string? name = null, url = null;
        var ld = LdJson.Match(html);
        if (ld.Success)
        {
            try
            {
                using var doc = JsonDocument.Parse(ld.Groups["json"].Value);
                if (doc.RootElement.TryGetProperty("publisher", out var pub) && pub.ValueKind == JsonValueKind.Object)
                {
                    name = Str(pub, "name");
                    url = Str(pub, "@id");
                }
            }
            catch (JsonException) { }
        }

        if (name is null)
        {
            var band = Band.Match(html);
            if (band.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(band.Groups["json"].Value));
                    name = Str(doc.RootElement, "name");
                }
                catch (JsonException) { }
            }
        }
        return (name is null ? null : Clean(name), url);
    }

    private static IReadOnlyList<string> ReadKeywords(string html)
    {
        var ld = LdJson.Match(html);
        if (!ld.Success) return [];
        try
        {
            using var doc = JsonDocument.Parse(ld.Groups["json"].Value);
            if (!doc.RootElement.TryGetProperty("keywords", out var k)) return [];
            var values = k.ValueKind switch
            {
                JsonValueKind.Array => k.EnumerateArray().Select(v => v.GetString() ?? ""),
                JsonValueKind.String => (k.GetString() ?? "").Split(','),
                _ => [],
            };
            return values.Select(v => v.Trim()).Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        value is not null
        && DateTimeOffset.TryParseExact(value, "dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static string? Absolute(Uri page, string? href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (!Uri.TryCreate(page, href, out var abs)) return null;
        if (abs.Scheme != Uri.UriSchemeHttps && abs.Scheme != Uri.UriSchemeHttp) return null;
        return new UriBuilder(abs) { Scheme = Uri.UriSchemeHttps, Port = -1, Query = "", Fragment = "" }.Uri.ToString();
    }

    private static string Clean(string s) =>
        Regex.Replace(WebUtility.HtmlDecode(s), @"\s+", " ").Trim();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
              : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s
              : null
            : null;
}
