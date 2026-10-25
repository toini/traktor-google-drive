using System.Text.Json;

namespace TraktorGoogleDrive.Discover;

/// <summary>
/// Toni's triage from before the app was deployed (wwwroot/discover/starter.json),
/// turned into ordinary log events once each (ADR 20261025-54 §4.3). The file is a
/// list of batches. A batch writes a "starter" event with its version, so a later
/// run, or another device reading the same log, never applies it again and never
/// undoes a change Toni made since. New entries go into a new batch.
/// </summary>
public sealed record StarterFile(IReadOnlyList<StarterBatch> Batches)
{
    public static StarterFile? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<StarterFile>(json, DiscoverState.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The events of every batch not yet in the log, in file order.</summary>
    public IReadOnlyList<DiscoverEvent> EventsFor(DiscoverState state) =>
        (Batches ?? []).SelectMany(b => b.EventsFor(state)).ToList();
}

public sealed record StarterBatch(
    string Version,
    IReadOnlyList<StarterBatch.SeedEntry>? Seeds,
    IReadOnlyList<StarterBatch.ReleaseEntry>? ReleasesHeard,
    IReadOnlyList<StarterBatch.TrackEntry>? Tracks)
{
    /// <param name="Watermark">"Heard through", a month or a day.</param>
    public sealed record SeedEntry(SeedKind Kind, string Name, string Genre, string? Url, string? Watermark,
        SeedOrigin? Origin, string? Reason);

    /// <param name="Id">A store's own release id, e.g. "beatport:4930160", kept as a second key.</param>
    public sealed record ReleaseEntry(string Genre, string? Label, string Release, string? ReleaseTitle,
        string? Artist = null, string? Catalog = null, string? Id = null);

    public sealed record TrackEntry(string Genre, string? Label, string Release, string? ReleaseTitle, string Track,
        Pass1? Pass1, int? Rating, Decision? Decision, string? Note);

    public IReadOnlyList<DiscoverEvent> EventsFor(DiscoverState state)
    {
        if (string.IsNullOrWhiteSpace(Version) || state.StarterVersions.Contains(Version)) return [];

        var events = new List<DiscoverEvent>();
        foreach (var s in Seeds ?? [])
        {
            var key = SeedCandidate.SeedKey(s.Kind, s.Name, s.Genre);
            events.Add(new DiscoverEvent
            {
                Type = "seed-add", At = default, SeedKey = key, Kind = s.Kind, Name = s.Name, Genre = s.Genre,
                Origin = s.Origin ?? SeedOrigin.Import, Reason = s.Reason, Url = s.Url,
            });
            if (s.Url is not null)
                events.Add(new DiscoverEvent { Type = "seed-url", At = default, SeedKey = key, Url = s.Url });
            if (DiscoverState.IsWatermark(s.Watermark))
                events.Add(new DiscoverEvent { Type = "watermark", At = default, SeedKey = key, Month = s.Watermark });
        }

        foreach (var r in ReleasesHeard ?? [])
        {
            // Title, label, artist and catalogue number let the same release on
            // another store count as heard too (ReleaseMatch).
            foreach (var key in new[] { Canonical(r.Release), r.Id }.Where(k => !string.IsNullOrWhiteSpace(k)))
                events.Add(new DiscoverEvent
                {
                    Type = "release-heard", At = default, ReleaseUrl = key, ReleaseTitle = r.ReleaseTitle,
                    Label = r.Label, Artist = r.Artist, Catalog = r.Catalog, Genre = r.Genre,
                });
        }

        foreach (var t in Tracks ?? [])
        {
            events.Add(new DiscoverEvent
            {
                Type = "track",
                At = default,
                TrackKey = DiscoverState.UrlTrackKey(t.Track),
                Url = Canonical(t.Track),
                ReleaseUrl = Canonical(t.Release),
                ReleaseTitle = t.ReleaseTitle,
                Label = t.Label,
                Genre = t.Genre,
                // Until the release page is read, the slug stands in for the title.
                Title = TitleFromSlug(t.Track),
                Pass1 = t.Pass1,
                Rating = t.Rating,
                Decision = t.Decision,
                Note = t.Note,
            });
        }
        events.Add(new DiscoverEvent { Type = "starter", At = default, Name = Version });
        return events;
    }

    public static string Canonical(string url) => DiscoverState.TrackUrlKey(url);

    public static string TitleFromSlug(string trackUrl)
    {
        var slug = Canonical(trackUrl).Split('/').Last();
        return slug.Length == 0 ? trackUrl : char.ToUpperInvariant(slug[0]) + slug[1..].Replace('-', ' ');
    }
}
