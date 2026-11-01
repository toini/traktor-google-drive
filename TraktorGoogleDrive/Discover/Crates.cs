using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Discover;

/// <summary>
/// A crate (ADR 20261025-54, "Crates"): a named list of tracks Toni wants to
/// find and hear, inside a genre but apart from its seed-driven queue, e.g. P →
/// "Looney Boon Hug Halloween 2026", a friend's set. Its items are known by
/// artist and title until a playable source is found for them.
/// </summary>
public sealed class Crate
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public required string Genre { get; init; }
    /// <summary>Where the list came from: "friend's set, SoundCloud Forest".</summary>
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<CrateItem> Items { get; } = [];
}

public sealed class CrateItem
{
    /// <summary>"crate:&lt;crate id&gt;:&lt;artist&gt;--&lt;title&gt;"; also the key of its triage ("track") events.</summary>
    public required string Key { get; init; }
    public required string CrateId { get; init; }
    public required string Artist { get; set; }
    public required string Title { get; set; }
    /// <summary>The account shown under the title where Toni found it (SoundCloud).</summary>
    public string? Uploader { get; set; }
    /// <summary>A release name from the source, when it showed one.</summary>
    public string? Release { get; set; }
    public string? Note { get; set; }
    /// <summary>A link that goes with the note: "Original for sale ↗", "Free on Ektoplazm ↗".</summary>
    public string? Link { get; set; }
    public int Pos { get; set; }
    public CrateResolution? Resolution { get; set; }

    public bool Truncated => CrateMatch.IsTruncated(Title);
}

[JsonConverter(typeof(JsonStringEnumConverter<CrateStatus>))]
public enum CrateStatus { Found, Ambiguous, NotFound }

/// <summary>A choice offered when a lookup could not tell: a Discogs release, or a track on a Bandcamp release.</summary>
public sealed record CrateCandidate(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string? Artist = null,
    [property: JsonPropertyName("label")] string? Label = null,
    [property: JsonPropertyName("year")] string? Year = null,
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("discogs")] long? DiscogsId = null,
    [property: JsonPropertyName("tid")] long? TrackId = null,
    [property: JsonPropertyName("release")] string? ReleaseUrl = null);

/// <summary>
/// What finding a crate item came to, saved in the log so every device sees it.
/// "Owned" is not stored: it is the collection's answer, asked every time.
/// </summary>
public sealed record CrateResolution
{
    [JsonPropertyName("status")] public CrateStatus Status { get; init; }
    /// <summary>
    /// "bandcamp" (plays in the player bar), "beatport" (a buy link only: Beatport's
    /// previews need API access, so it is heard on Beatport) or "soundcloud" (plays
    /// in its widget, behind <c>DiscoverFeatures.SoundCloud</c>).
    /// </summary>
    [JsonPropertyName("src")] public string? Source { get; init; }
    /// <summary>The track's page: a Bandcamp track or release, or a SoundCloud track.</summary>
    [JsonPropertyName("page")] public string? PageUrl { get; init; }
    /// <summary>The Bandcamp page the track was read from, for the audio endpoint's <c>?release=</c>.</summary>
    [JsonPropertyName("release")] public string? ReleaseUrl { get; init; }
    [JsonPropertyName("tid")] public long? TrackId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("rtitle")] public string? ReleaseTitle { get; init; }
    [JsonPropertyName("date")] public string? Released { get; init; }
    [JsonPropertyName("dur")] public double? Duration { get; init; }
    /// <summary>The track on Beatport, as a second buy link beside Bandcamp, or the only one.</summary>
    [JsonPropertyName("bp")] public string? BeatportUrl { get; init; }
    /// <summary>Free or name-your-price on its store.</summary>
    [JsonPropertyName("free")] public bool? Free { get; init; }
    /// <summary>How it was found: "discogs", "guess", "pasted", "picked", "oembed", "research" (a shipped starter batch).</summary>
    [JsonPropertyName("via")] public string? Via { get; init; }
    [JsonPropertyName("discogs")] public string? DiscogsUrl { get; init; }
    /// <summary>"Artist – Release (Label, 2024)" on Discogs, shown when nothing plays.</summary>
    [JsonPropertyName("dtitle")] public string? DiscogsTitle { get; init; }
    [JsonPropertyName("cands")] public IReadOnlyList<CrateCandidate>? Candidates { get; init; }
    [JsonPropertyName("msg")] public string? Message { get; init; }
}

/// <summary>Where one crate item stands, as shown and filtered on.</summary>
public enum CrateItemState { Pending, Found, Ambiguous, NotFound, Owned }

/// <summary>
/// What Toni made of a crate track by ear: the "track" events the list's player
/// writes, read back as one badge. Skipping is "heard once, not for me".
/// </summary>
public enum CrateVerdict { Unheard, Skipped, Maybe, Liked, Rated, Buy, Bought }

/// <summary>The chips over a crate's list. "To hear" is the default, so skipped tracks get out of the way.</summary>
public enum CrateFilter { ToHear, Skipped, Maybe, Liked, ToBuy, NotFound, All }

public static class CrateVerdicts
{
    /// <summary>The strongest thing said about a track: bought, then buy, skip, a rating, like, maybe.</summary>
    public static CrateVerdict Of(TrackState? t) => t switch
    {
        null => CrateVerdict.Unheard,
        { Bought: true } => CrateVerdict.Bought,
        { Decision: Decision.Buy } => CrateVerdict.Buy,
        { Pass1: Pass1.Skip } => CrateVerdict.Skipped,
        { Rating: >= 1 } => CrateVerdict.Rated,
        { Pass1: Pass1.Like } => CrateVerdict.Liked,
        { Pass1: Pass1.Maybe } => CrateVerdict.Maybe,
        _ => CrateVerdict.Unheard,
    };

    public static bool Matches(CrateFilter filter, CrateItemState state, CrateVerdict verdict) => filter switch
    {
        CrateFilter.ToHear => verdict == CrateVerdict.Unheard && state != CrateItemState.Owned,
        CrateFilter.Skipped => verdict == CrateVerdict.Skipped,
        CrateFilter.Maybe => verdict == CrateVerdict.Maybe,
        CrateFilter.Liked => verdict is CrateVerdict.Liked or CrateVerdict.Rated,
        CrateFilter.ToBuy => verdict == CrateVerdict.Buy,
        CrateFilter.NotFound => state is CrateItemState.Pending or CrateItemState.NotFound or CrateItemState.Ambiguous,
        _ => true,
    };

    public static string Label(CrateFilter f) => f switch
    {
        CrateFilter.ToHear => "To hear",
        CrateFilter.Skipped => "Skipped",
        CrateFilter.Maybe => "Maybe",
        CrateFilter.Liked => "Liked",
        CrateFilter.ToBuy => "To buy",
        CrateFilter.NotFound => "Not found",
        _ => "All",
    };

    /// <summary>The badge: "Unheard", "✗ Skipped", "? Maybe", "♥ Liked", "★4", "🛒 Buy", "✓ Bought".</summary>
    public static string Badge(CrateVerdict v, int? rating) => v switch
    {
        CrateVerdict.Skipped => "✗ Skipped",
        CrateVerdict.Maybe => "? Maybe",
        CrateVerdict.Liked => "♥ Liked",
        CrateVerdict.Rated => "★" + rating,
        CrateVerdict.Buy => "🛒 Buy",
        CrateVerdict.Bought => "✓ Bought",
        _ => "Unheard",
    };

    public static string Explain(CrateVerdict v) => v switch
    {
        CrateVerdict.Skipped => "Heard once, not for me",
        CrateVerdict.Maybe => "Heard, maybe: decide later",
        CrateVerdict.Liked => "Heard and liked: rate it",
        CrateVerdict.Rated => "Rated",
        CrateVerdict.Buy => "On the buy list",
        CrateVerdict.Bought => "Bought",
        _ => "Not heard yet",
    };
}

/// <summary>Keys, names and title matching for crates.</summary>
public static partial class CrateMatch
{
    public const string KeyPrefix = "crate:";

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    [GeneratedRegex(@"\(\s*truncated\s*\)|\btruncated\)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TruncatedMark();

    public static string Slug(string? text) => NonSlug().Replace(Names.Normalize(text), "-").Trim('-');

    public static string ItemKey(string crateId, string artist, string title) =>
        $"{KeyPrefix}{crateId}:{Slug(artist)}--{Slug(title)}";

    /// <summary>The crate an item or track key belongs to, or null for any other key.</summary>
    public static string? CrateOf(string? key)
    {
        if (key is null || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return null;
        var end = key.IndexOf(':', KeyPrefix.Length);
        return end > KeyPrefix.Length ? key[KeyPrefix.Length..end] : null;
    }

    /// <summary>A title cut off in the screenshots: "Self R… (truncated)".</summary>
    public static bool IsTruncated(string? title) =>
        title is not null && (title.Contains('…') || title.Contains("truncated", StringComparison.OrdinalIgnoreCase));

    /// <summary>What can be searched for: the part before "…", without the "(truncated)" mark.</summary>
    public static string SearchTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title;
        var cut = t.IndexOf('…');
        if (cut >= 0) t = t[..cut];
        t = TruncatedMark().Replace(t, " ").Trim();
        // An opening bracket the cut left unclosed: "The Cosmos Welcomes You (Psilocybin Intellig".
        var open = t.LastIndexOf('(');
        if (open >= 0 && t.IndexOf(')', open) < 0 && IsTruncated(title)) t = t[..open].Trim();
        return t;
    }

    /// <summary>
    /// How well a store's track title fits an item: 3 the same title (with an
    /// artist in common), 2 the same title, 2 "Artist - Title" as Bandcamp
    /// compilations write it, 1 a truncated title's start; 0 no match.
    /// </summary>
    public static int TitleScore(CrateItem item, string candidateTitle, string? candidateArtist)
    {
        var wanted = Names.TitleKey(SearchTitle(item.Title));
        var got = Names.TitleKey(candidateTitle);
        if (wanted.Length == 0 || got.Length == 0) return 0;
        var artists = Names.ArtistKeys(item.Artist);
        var sameArtist = Names.ArtistKeys(candidateArtist).Overlaps(artists);
        if (got == wanted) return sameArtist ? 3 : 2;
        if (got.EndsWith(" " + wanted, StringComparison.Ordinal) && artists.Any(a => got.StartsWith(a + " ", StringComparison.Ordinal)))
            return 2;
        if (item.Truncated && wanted.Length >= 3 && (got.StartsWith(wanted, StringComparison.Ordinal)
                                                     || artists.Any(a => got.StartsWith(a + " " + wanted, StringComparison.Ordinal))))
            return 1;
        return 0;
    }

    /// <summary>A release in a label's list worth opening for this item: its title is the track's (a single) or Discogs' release.</summary>
    public static bool ReleaseWorthOpening(CrateItem item, string releaseTitle, string? discogsReleaseTitle)
    {
        var r = Names.TitleKey(releaseTitle);
        if (r.Length == 0) return false;
        var t = Names.TitleKey(SearchTitle(item.Title));
        if (t.Length > 0 && (r == t || r.Contains(t, StringComparison.Ordinal) || (item.Truncated && r.StartsWith(t, StringComparison.Ordinal))))
            return true;
        var d = Names.TitleKey(discogsReleaseTitle);
        return d.Length > 0 && (r == d || r.Contains(d, StringComparison.Ordinal) || d.Contains(r, StringComparison.Ordinal));
    }

    /// <summary>Bandcamp's search page, opened in Toni's own browser tab. The server never calls it (robots.txt).</summary>
    public static string BandcampSearchUrl(CrateItem item) =>
        "https://bandcamp.com/search?item_type=t&q=" + Uri.EscapeDataString($"{FirstArtist(item.Artist)} {SearchTitle(item.Title)}".Trim());

    /// <summary>Beatport's search page, likewise in Toni's tab; the app never fetches Beatport.</summary>
    public static string BeatportSearchUrl(CrateItem item) =>
        "https://www.beatport.com/search/tracks?q=" + Uri.EscapeDataString($"{FirstArtist(item.Artist)} {SearchTitle(item.Title)}".Trim());

    /// <summary>A Beatport track or release page: https://www.beatport.com/track/&lt;slug&gt;/&lt;id&gt;.</summary>
    public static bool IsBeatportUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && (u.Host.Equals("beatport.com", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.beatport.com", StringComparison.OrdinalIgnoreCase))
        && BeatportPath().IsMatch(u.AbsolutePath);

    [GeneratedRegex(@"^/(?:[a-z]{2}/)?(track|release)/[^/]+/\d+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex BeatportPath();

    /// <summary>SoundCloud's search page, likewise in Toni's tab.</summary>
    public static string SoundCloudSearchUrl(CrateItem item) =>
        "https://soundcloud.com/search/sounds?q=" + Uri.EscapeDataString($"{FirstArtist(item.Artist)} {SearchTitle(item.Title)}".Trim());

    public static string FirstArtist(string artist) =>
        Names.SplitArtists(Regex.Replace(artist, @"\([^)]*\)", " ")).FirstOrDefault() ?? artist;

    /// <summary>The Bandcamp subdomains worth a checked guess: the first artist, then the uploader's names.</summary>
    public static IReadOnlyList<string> BandcampGuesses(CrateItem item, int max = 2)
    {
        var names = new List<string> { FirstArtist(item.Artist) };
        if (!string.IsNullOrWhiteSpace(item.Uploader))
            names.AddRange(Regex.Split(item.Uploader, @"\s*,\s*|\s+-\s+").Select(n => Regex.Replace(n, @"\(.*$", "")));
        return names.Select(n => Names.BandcampSlugGuess(n))
            .Where(s => s.Length is > 1 and <= 63 && Regex.IsMatch(s, "^[a-z0-9][a-z0-9-]*$"))
            .Distinct()
            .Take(max)
            .Select(s => $"https://{s}.bandcamp.com")
            .ToList();
    }

    /// <summary>The widget's address for a SoundCloud track (the official embed player).</summary>
    public static string SoundCloudWidgetUrl(string trackPage) =>
        "https://w.soundcloud.com/player/?url=" + Uri.EscapeDataString(trackPage)
        + "&auto_play=true&hide_related=true&show_comments=false&show_reposts=false&show_teaser=false&visual=false";
}
