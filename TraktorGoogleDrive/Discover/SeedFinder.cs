using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Discover;

/// <summary>A collection track as seed finding sees it. Built from the parsed NML by <c>DiscoverService</c>.</summary>
/// <param name="Stars">0–5, from RANKING (51 per star).</param>
/// <param name="Genres">Genre folders whose playlists hold the track, most playlists first.</param>
public sealed record CollectionTrack(
    string Artist,
    string Title,
    string? Label,
    int Stars,
    int Plays,
    DateOnly? Imported,
    DateOnly? LastPlayed,
    string? Comment,
    IReadOnlyList<string> Genres);

public enum SeedKind { Label, Artist }

/// <summary>Why a seed was proposed. Order is priority: "eti lisää" first (ADR decision 7).</summary>
public enum SeedOrigin { EtiLisaa = 0, Playlist = 1, TopList = 2, Manual = 3, Import = 4 }

public sealed record SeedCandidate(
    SeedKind Kind,
    string Name,
    string Genre,
    SeedOrigin Origin,
    int Tracks,
    int Rated4,
    int Plays,
    DateOnly? LastActivity,
    double Score,
    string Reason)
{
    public string Key => SeedKey(Kind, Name, Genre);

    public static string SeedKey(SeedKind kind, string name, string genre) =>
        $"{(kind == SeedKind.Label ? "label" : "artist")}:{Names.EntityKey(name)}:{genre}";
}

/// <summary>
/// Turns the collection into seed candidates (ADR 20261025-54 §3). Pure: the
/// same tracks and day always give the same list, which is what the tests pin.
/// </summary>
public static partial class SeedFinder
{
    /// <summary>"eti lisää", "Etsi lisää", "ETI LISÄÄ", "eti lisaa". Not "lisää luuppi" ("add a loop").</summary>
    [GeneratedRegex(@"\bets?i\s+lis(?:ää|aa|äa|aä)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EtiLisaaPattern();

    public static bool SaysEtiLisaa(string? comment) => comment is not null && EtiLisaaPattern().IsMatch(comment);

    /// <summary>
    /// Every track whose comment asks to "search for more" seeds its artist(s) and
    /// its label, in the genre whose playlists hold it most.
    /// </summary>
    public static IReadOnlyList<SeedCandidate> EtiLisaa(IEnumerable<CollectionTrack> collection)
    {
        var result = new Dictionary<string, SeedCandidate>();
        foreach (var t in collection)
        {
            if (!SaysEtiLisaa(t.Comment) || t.Genres.Count == 0) continue;
            var genre = t.Genres[0];
            var reason = $"“{t.Comment!.Trim()}” on {t.Artist} – {t.Title}";

            foreach (var artist in Names.SplitArtists(t.Artist))
                Add(new SeedCandidate(SeedKind.Artist, artist, genre, SeedOrigin.EtiLisaa, 1, t.Stars >= 4 ? 1 : 0,
                    t.Plays, Latest(t.Imported, t.LastPlayed), 1000, reason));
            if (!string.IsNullOrWhiteSpace(t.Label) && !Names.IsStopName(t.Label))
                Add(new SeedCandidate(SeedKind.Label, t.Label.Trim(), genre, SeedOrigin.EtiLisaa, 1, t.Stars >= 4 ? 1 : 0,
                    t.Plays, Latest(t.Imported, t.LastPlayed), 1000, reason));
        }
        return result.Values.OrderBy(c => c.Genre).ThenBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

        void Add(SeedCandidate c) => result.TryAdd(c.Key, c);
    }

    /// <summary>
    /// The artists and labels of a playlist's well-rated tracks (ADR §3.2): "take
    /// the H16 tracklist, find the tracks with a good rating and use the artist or
    /// label for searching". Each track counts once, however often the playlist holds it.
    /// </summary>
    public static IReadOnlyList<SeedCandidate> FromPlaylist(
        IEnumerable<CollectionTrack> playlistTracks, string genre, string playlistName,
        int minStars = 4, bool artists = true, bool labels = true)
    {
        var picked = playlistTracks.Distinct().Where(t => t.Stars >= minStars).ToList();
        var reason = $"{playlistName}, tracks rated {minStars}★ or more";
        return Tally(picked, genre, SeedOrigin.Playlist, artists, labels, reason, today: null)
            .OrderByDescending(c => c.Tracks).ThenBy(c => c.Kind).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Per-genre top lists (ADR §3.1, §3.3): an artist or label is a candidate in a
    /// genre when it has ≥ 2 tracks, or ≥ 1 track rated ≥ 4★, in that genre's
    /// playlists. Ranked by 3 × rated-4★ + plays / 20 + recency.
    /// </summary>
    public static IReadOnlyList<SeedCandidate> TopList(
        IEnumerable<CollectionTrack> collection, string genre, SeedKind kind, DateOnly today, int take = 30)
    {
        var inGenre = collection.Where(t => t.Genres.Contains(genre)).ToList();
        return Tally(inGenre, genre, SeedOrigin.TopList, kind == SeedKind.Artist, kind == SeedKind.Label, "", today)
            .Where(c => c.Tracks >= 2 || c.Rated4 >= 1)
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.Tracks).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }

    public static double Recency(DateOnly? last, DateOnly today)
    {
        if (last is null) return 0;
        var days = today.DayNumber - last.Value.DayNumber;
        if (days <= 365) return 5;
        if (days >= 5 * 365) return 0;
        return 5.0 * (5 * 365 - days) / (4 * 365);
    }

    private static IEnumerable<SeedCandidate> Tally(
        IReadOnlyList<CollectionTrack> tracks, string genre, SeedOrigin origin,
        bool artists, bool labels, string reason, DateOnly? today)
    {
        var acc = new Dictionary<string, (SeedKind Kind, Dictionary<string, int> Spellings, int Tracks, int Rated4, int Plays, DateOnly? Last)>();

        foreach (var t in tracks)
        {
            if (artists)
                foreach (var a in Names.SplitArtists(t.Artist)) Count(SeedKind.Artist, a, t);
            if (labels && !string.IsNullOrWhiteSpace(t.Label) && !Names.IsStopName(t.Label))
                Count(SeedKind.Label, t.Label.Trim(), t);
        }

        foreach (var (key, v) in acc)
        {
            var name = v.Spellings.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).First().Key;
            var score = 3.0 * v.Rated4 + v.Plays / 20.0 + (today is { } d ? Recency(v.Last, d) : 0);
            var why = reason.Length > 0 ? reason
                : $"{v.Tracks} track{(v.Tracks == 1 ? "" : "s")}, {v.Rated4} rated 4★+, {v.Plays} plays";
            yield return new SeedCandidate(v.Kind, name, genre, origin, v.Tracks, v.Rated4, v.Plays, v.Last,
                Math.Round(score, 2), why);
        }

        void Count(SeedKind kind, string name, CollectionTrack t)
        {
            var key = SeedCandidate.SeedKey(kind, name, genre);
            if (!acc.TryGetValue(key, out var v)) v = (kind, new Dictionary<string, int>(), 0, 0, 0, null);
            v.Spellings[name] = v.Spellings.GetValueOrDefault(name) + 1;
            v.Tracks++;
            if (t.Stars >= 4) v.Rated4++;
            v.Plays += t.Plays;
            v.Last = Latest(v.Last, Latest(t.Imported, t.LastPlayed));
            acc[key] = v;
        }
    }

    private static DateOnly? Latest(DateOnly? a, DateOnly? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
