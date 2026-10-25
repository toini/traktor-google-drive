namespace TraktorGoogleDrive.Discover;

/// <summary>A track of a store release, as the client keeps it.</summary>
public sealed record ReleaseTrack(long TrackId, int? Number, string Title, string Artist, double Duration,
    string? TrackUrl, bool Playable);

/// <summary>A store release, with the seed that found it.</summary>
public sealed record ReleaseInfo(
    string Source,
    string Url,
    string Title,
    string Artist,
    string? Label,
    DateOnly? Date,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ReleaseTrack> Tracks,
    string SeedKey)
{
    public static string TrackKey(string source, long trackId) => $"{source}:{trackId}";
}

public sealed record QueueItem(string TrackKey, ReleaseInfo Release, ReleaseTrack Track);

/// <summary>
/// Tracks Toni already owns (ADR 20261025-54 §6.2): never queued. Matching is by
/// normalised title ("(Original Mix)" and featured artists dropped, other mix
/// names kept) plus at least one shared artist.
/// </summary>
public sealed class OwnedIndex
{
    private readonly Dictionary<string, List<IReadOnlySet<string>>> _byTitle = new(StringComparer.Ordinal);

    public OwnedIndex(IEnumerable<(string Artist, string Title)> collection)
    {
        foreach (var (artist, title) in collection)
        {
            var key = Names.TitleKey(title);
            if (key.Length == 0) continue;
            if (!_byTitle.TryGetValue(key, out var list)) _byTitle[key] = list = [];
            list.Add(Names.ArtistKeys(artist));
        }
    }

    public int Count => _byTitle.Values.Sum(l => l.Count);

    public bool Owns(string artist, string title)
    {
        if (!_byTitle.TryGetValue(Names.TitleKey(title), out var candidates)) return false;
        var artists = Names.ArtistKeys(artist);
        return candidates.Any(c => c.Overlaps(artists));
    }
}

/// <summary>
/// The Listen queue for one genre (ADR §4, §5.2): every track of a fetched
/// release that Toni has not heard, does not own, and that is newer than its
/// seed's watermark; oldest release first, then track order.
/// </summary>
public static class QueueBuilder
{
    public static IReadOnlyList<QueueItem> Build(
        DiscoverState state, string genre, IEnumerable<ReleaseInfo> releases, OwnedIndex owned)
    {
        var items = new List<QueueItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var heardUrls = state.HeardTrackUrls;

        var ordered = releases
            .Where(r => state.Seeds.TryGetValue(r.SeedKey, out var seed) && seed.Genre == genre)
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Date is null ? 1 : 0)
            .ThenBy(r => r.Date)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase);

        foreach (var release in ordered)
        {
            if (state.IsReleaseHeard(release.Url, release.Title, release.Label, release.Artist)) continue;
            var seed = state.Seeds[release.SeedKey];
            if (DiscoverState.BeforeWatermark(release.Date, seed.Watermark)) continue;

            foreach (var track in release.Tracks.OrderBy(t => t.Number ?? int.MaxValue))
            {
                var key = ReleaseInfo.TrackKey(release.Source, track.TrackId);
                if (!seen.Add(key)) continue;
                if (state.Tracks.ContainsKey(key)) continue;          // heard, whatever the verdict
                if (track.TrackUrl is { } u && heardUrls.Contains(DiscoverState.TrackUrlKey(u))) continue;  // heard, known by URL
                if (owned.Owns(track.Artist, track.Title)) continue;  // already in the collection
                items.Add(new QueueItem(key, release, track));
            }
        }
        return items;
    }

    /// <summary>
    /// The month a seed can be marked "heard through": the newest month whose
    /// releases, and all older ones, have no unheard track left. Null when nothing advances.
    /// </summary>
    public static string? HeardThrough(DiscoverState state, Seed seed, IEnumerable<ReleaseInfo> releases, OwnedIndex owned)
    {
        string? through = null;
        var heardUrls = state.HeardTrackUrls;
        var months = releases
            .Where(r => r.SeedKey == seed.Key && r.Date is not null)
            .GroupBy(r => r.Date!.Value.ToString("yyyy-MM"))
            .OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var month in months)
        {
            var done = month.All(release =>
                state.IsReleaseHeard(release.Url, release.Title, release.Label, release.Artist)
                || DiscoverState.BeforeWatermark(release.Date, seed.Watermark)
                || release.Tracks.All(t =>
                    state.Tracks.ContainsKey(ReleaseInfo.TrackKey(release.Source, t.TrackId))
                    || (t.TrackUrl is { } u && heardUrls.Contains(DiscoverState.TrackUrlKey(u)))
                    || owned.Owns(t.Artist, t.Title)));
            if (!done) break;
            through = month.Key;
        }
        return through is not null
               && (DiscoverState.LastDay(seed.Watermark) is not { } current || DiscoverState.LastDay(through) > current)
            ? through
            : null;
    }
}
