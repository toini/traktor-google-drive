using System.Collections.Concurrent;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>What a lookup found: the ranked candidates, and the best one's details.</summary>
public sealed record DiscogsLookup(
    string Artist, string Title, IReadOnlyList<DiscogsCandidate> Candidates, bool Ambiguous, DiscogsMatch? Best);

/// <param name="TrackFound">The release's tracklist has the title (it may be a compilation that only matched the artist).</param>
/// <param name="BandcampUrls">Canonical Bandcamp pages from the release notes, then its labels' and artists' profiles.</param>
public sealed record DiscogsMatch(
    long Id, string Title, string Artists, IReadOnlyList<string> Labels, string? Released, string? WebUrl,
    bool TrackFound, string? TrackTitle, IReadOnlyList<string> BandcampUrls);

/// <summary>
/// Discogs, read on Toni's action only (ADR 20261025-54 §1.3): unauthenticated
/// unless a personal token is configured, one request per 2.6 s through the
/// <see cref="PoliteFetcher"/>, and cached for a week, so "Find all" run twice
/// costs nothing the second time. Only the four URL shapes below are fetched.
/// </summary>
public sealed class DiscogsSource
{
    public const string Api = "https://api.discogs.com";
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(7);
    private const int MaxCached = 4000;

    private readonly PoliteFetcher _fetcher;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (string Json, DateTimeOffset At)> _cache = new();

    public DiscogsSource(PoliteFetcher fetcher, TimeProvider clock)
    {
        _fetcher = fetcher;
        _clock = clock;
    }

    public PoliteFetcher Fetcher => _fetcher;

    public static Uri SearchUrl(string artist, string title) =>
        new($"{Api}/database/search?type=release&artist={Uri.EscapeDataString(artist)}&track={Uri.EscapeDataString(title)}&per_page=10");

    public static Uri ReleaseUrl(long id) => new($"{Api}/releases/{id}");
    public static Uri LabelUrl(long id) => new($"{Api}/labels/{id}");
    public static Uri ArtistUrl(long id) => new($"{Api}/artists/{id}");

    /// <summary>
    /// Searches by the first artist and the title, ranks, and unless that is
    /// ambiguous (or <paramref name="releaseId"/> names Toni's pick) reads the best
    /// release and its labels' and artists' links. 1 to 4 requests, fewer when cached.
    /// </summary>
    public async Task<DiscogsLookup> LookupAsync(string artist, string title, long? releaseId, CancellationToken ct)
    {
        var firstArtist = FirstArtist(artist);
        var query = DiscogsNames.SearchTitle(title);
        var ranked = query.Length == 0 || firstArtist.Length == 0
            ? []
            : DiscogsRanking.Rank(DiscogsParser.ParseSearch(await GetAsync(SearchUrl(firstArtist, query), ct)), artist, title);

        var ambiguous = releaseId is null && DiscogsRanking.IsAmbiguous(ranked);
        var pick = releaseId ?? (ambiguous || ranked.Count == 0 ? null : ranked[0].Id);
        DiscogsMatch? best = pick is { } id ? await MatchAsync(id, artist, title, ct) : null;
        return new DiscogsLookup(artist, title, ranked.Take(6).ToList(), ambiguous, best);
    }

    /// <summary>"The Hierophants (Via Axis, Purist &amp; Yachay)" → "The Hierophants"; "Raveen ft. Mafia" → "Raveen".</summary>
    public static string FirstArtist(string artist)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(artist, @"\([^)]*\)", " ");
        var first = System.Text.RegularExpressions.Regex.Split(plain, @"\s*(?:,|&|\bfeat\.?\s|\bft\.?\s|\bvs\.?\s)\s*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)[0];
        return first.Trim();
    }

    private async Task<DiscogsMatch?> MatchAsync(long id, string artist, string title, CancellationToken ct)
    {
        if (DiscogsParser.ParseRelease(await GetAsync(ReleaseUrl(id), ct)) is not { } release) return null;
        var truncated = DiscogsNames.IsTruncated(title);
        var track = release.Tracklist.FirstOrDefault(t => DiscogsNames.TitleMatches(title, t.Title, truncated));

        var urls = new List<string>(DiscogsParser.BandcampPages(release.NoteUrls));
        foreach (var label in release.Labels.Where(l => l.Id > 0).DistinctBy(l => l.Id).Take(2))
            if (await ProfileAsync(LabelUrl(label.Id), ct) is { } p) urls.AddRange(DiscogsParser.BandcampPages(p.Urls));

        // The item's own artists, as credited on the release or on its track.
        var wanted = DiscogsNames.ArtistKeys(artist);
        if (urls.Count == 0)
            foreach (var credit in release.Artists.Where(a => a.Id > 0 && DiscogsNames.ArtistKeys(a.Name).Overlaps(wanted)).Take(1))
                if (await ProfileAsync(ArtistUrl(credit.Id), ct) is { } p) urls.AddRange(DiscogsParser.BandcampPages(p.Urls));

        return new DiscogsMatch(release.Id, release.Title, string.Join(", ", release.Artists.Select(a => a.Name)),
            release.Labels.Select(l => l.Name).Distinct().ToList(), release.Released ?? release.Year, release.Uri,
            track is not null, track?.Title, urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private async Task<DiscogsProfile?> ProfileAsync(Uri url, CancellationToken ct)
    {
        try { return DiscogsParser.ParseProfile(await GetAsync(url, ct)); }
        catch (FetchFailedException e) when (e.Status == 404) { return null; }
    }

    private async Task<string> GetAsync(Uri url, CancellationToken ct)
    {
        var key = url.ToString();
        var now = _clock.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < Ttl) return hit.Json;
        var json = await _fetcher.GetStringAsync(url, ct);
        if (_cache.Count >= MaxCached) foreach (var k in _cache.Keys.Take(MaxCached / 4)) _cache.TryRemove(k, out _);
        _cache[key] = (json, now);
        return json;
    }
}
