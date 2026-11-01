namespace TraktorGoogleDrive.Server.Discover;

public sealed record DiscogsCandidate(
    long Id, string Artist, string Title, string? Year, IReadOnlyList<string> Labels, string? Catno,
    IReadOnlyList<string> Formats, string? WebUrl, int Score);

/// <summary>
/// Which Discogs search result is the track a crate item names (ADR 20261025-54,
/// "Crates"). The search already filters by artist and track title; what is left
/// to decide is whose release it is.
/// </summary>
public static class DiscogsRanking
{
    public const string Web = "https://www.discogs.com";

    /// <summary>
    /// Score: every artist of the item credited 4, some of them 3 (one artist's
    /// album with the collaboration on it), a compilation ("Various") 2, none
    /// dropped; a single named after the track +1; a digital ("File") release +1.
    /// Ties go to the newer release, then the lower id.
    /// </summary>
    public static IReadOnlyList<DiscogsCandidate> Rank(IEnumerable<DiscogsSearchResult> results, string artist, string title)
    {
        var wanted = DiscogsNames.ArtistKeys(artist);
        var list = new List<DiscogsCandidate>();
        foreach (var r in results)
        {
            var credited = DiscogsNames.ArtistKeys(r.Artist);
            int score;
            if (wanted.Count > 0 && wanted.IsSubsetOf(credited)) score = 4;
            else if (wanted.Overlaps(credited)) score = 3;
            else if (DiscogsNames.IsVarious(r.Artist)) score = 2;
            else continue;
            if (DiscogsNames.TitleMatches(title, r.Title, truncated: false)) score += 1;
            if (r.Formats.Any(f => f.Equals("File", StringComparison.OrdinalIgnoreCase))) score += 1;
            list.Add(new DiscogsCandidate(r.Id, r.Artist, r.Title, r.Year, r.Labels, r.Catno, r.Formats,
                r.Uri is null ? null : r.Uri.StartsWith("http", StringComparison.Ordinal) ? r.Uri : Web + r.Uri, score));
        }
        return list
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => int.TryParse(c.Year, out var y) ? y : 0)
            .ThenBy(c => c.Id)
            .ToList();
    }

    /// <summary>
    /// Ambiguous when the two best share a score but are credited to different
    /// identities: "Nobot" and "Nobot (2)" are two artists, so neither can be
    /// picked for Toni. A compilation and an EP of the same artist are not.
    /// </summary>
    public static bool IsAmbiguous(IReadOnlyList<DiscogsCandidate> ranked)
    {
        if (ranked.Count < 2 || ranked[0].Score != ranked[1].Score) return false;
        var (a, b) = (ranked[0], ranked[1]);
        if (DiscogsNames.IsVarious(a.Artist) || DiscogsNames.IsVarious(b.Artist)) return false;
        // The same names, numbered apart by Discogs: two different artists.
        return DiscogsNames.ArtistKeys(a.Artist).SetEquals(DiscogsNames.ArtistKeys(b.Artist))
               && !DiscogsNames.Identities(a.Artist).SetEquals(DiscogsNames.Identities(b.Artist));
    }
}
