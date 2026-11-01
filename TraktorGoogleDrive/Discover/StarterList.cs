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

    /// <summary>
    /// The events of every batch not yet in the log, in file order. A later batch
    /// may resolve items an earlier one adds in the same run, so the new items'
    /// keys are carried from batch to batch.
    /// </summary>
    public IReadOnlyList<DiscoverEvent> EventsFor(DiscoverState state)
    {
        var added = new HashSet<string>(StringComparer.Ordinal);
        var events = new List<DiscoverEvent>();
        foreach (var b in Batches ?? [])
        {
            var batch = b.EventsFor(state, added);
            foreach (var e in batch.Where(e => e is { Type: "crate-item", TrackKey: not null })) added.Add(e.TrackKey!);
            events.AddRange(batch);
        }
        return events;
    }
}

public sealed record StarterBatch(
    string Version,
    IReadOnlyList<StarterBatch.SeedEntry>? Seeds,
    IReadOnlyList<StarterBatch.ReleaseEntry>? ReleasesHeard,
    IReadOnlyList<StarterBatch.TrackEntry>? Tracks,
    IReadOnlyList<StarterBatch.CrateEntry>? Crates = null,
    IReadOnlyList<StarterBatch.ResolutionEntry>? Resolutions = null)
{
    /// <summary>
    /// A buy link found for a crate item outside the app (a web search for the
    /// tracks Find left unresolved): a Bandcamp page, a Beatport track, or both.
    /// Applied as a "crate-resolve" with via "research", only to an item Toni has
    /// not resolved, pasted, picked or given a verdict.
    /// </summary>
    /// <param name="Artist">The item's artist as the crate has it (its key), not a corrected spelling.</param>
    /// <param name="Note">The search's own words, shown as a tooltip.</param>
    /// <param name="FixArtist">A corrected artist ("Pepe's Bodega"); the key stays, the screenshot text goes into <paramref name="ItemNote"/>.</param>
    /// <param name="FixTitle">A corrected or completed title ("Self R…" → "Self Realization").</param>
    /// <param name="ItemNote">The item's note after this batch, shown on its row ("Gidra's own edit, not for sale. Original for sale").</param>
    /// <param name="Link">A link that goes with the item's note ("Original for sale ↗").</param>
    /// <param name="Free">Free or name-your-price.</param>
    public sealed record ResolutionEntry(string Crate, string Artist, string Title, string? Bandcamp = null, string? Beatport = null,
        string? Release = null, string? Label = null, string? Date = null, string? Confidence = null, string? Note = null,
        string? FixArtist = null, string? FixTitle = null, string? ItemNote = null, string? Link = null, bool? Free = null);

    /// <summary>A crate shipped with its tracks (ADR "Crates"): a friend's set, say.</summary>
    public sealed record CrateEntry(string Id, string Name, string Genre, string? Note, IReadOnlyList<CrateTrackEntry>? Tracks);

    public sealed record CrateTrackEntry(string Artist, string Title, string? Uploader, string? Release = null,
        string? TrackNumber = null, int? Bpm = null, string? Note = null);

    /// <param name="Watermark">"Heard through", a month or a day.</param>
    public sealed record SeedEntry(SeedKind Kind, string Name, string Genre, string? Url, string? Watermark,
        SeedOrigin? Origin, string? Reason);

    /// <param name="Id">A store's own release id, e.g. "beatport:4930160", kept as a second key.</param>
    public sealed record ReleaseEntry(string Genre, string? Label, string Release, string? ReleaseTitle,
        string? Artist = null, string? Catalog = null, string? Id = null);

    public sealed record TrackEntry(string Genre, string? Label, string Release, string? ReleaseTitle, string Track,
        Pass1? Pass1, int? Rating, Decision? Decision, string? Note);

    /// <param name="addedEarlier">Crate items an earlier batch of the same run adds: new, so nothing of Toni's is on them.</param>
    public IReadOnlyList<DiscoverEvent> EventsFor(DiscoverState state, IReadOnlySet<string>? addedEarlier = null)
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
        foreach (var c in Crates ?? [])
        {
            events.Add(new DiscoverEvent { Type = "crate-add", At = default, CrateId = c.Id, Name = c.Name, Genre = c.Genre, Note = c.Note });
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var pos = 0;
            foreach (var t in c.Tracks ?? [])
            {
                var key = CrateMatch.ItemKey(c.Id, t.Artist, t.Title);
                for (var n = 2; !keys.Add(key); n++) key = CrateMatch.ItemKey(c.Id, t.Artist, t.Title) + "-" + n;
                var notes = new[] { t.Note, t.Bpm is { } bpm ? $"{bpm} BPM" : null, t.TrackNumber is { } no ? $"track {no}" : null }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                events.Add(new DiscoverEvent
                {
                    Type = "crate-item", At = default, CrateId = c.Id, TrackKey = key, Artist = t.Artist, Title = t.Title,
                    Uploader = t.Uploader, ReleaseTitle = t.Release, Note = notes.Count > 0 ? string.Join(" · ", notes) : null,
                    Pos = pos++,
                });
            }
        }
        var added = new HashSet<string>(addedEarlier ?? new HashSet<string>(), StringComparer.Ordinal);
        foreach (var e in events.Where(e => e is { Type: "crate-item", TrackKey: not null })) added.Add(e.TrackKey!);
        foreach (var r in Resolutions ?? [])
        {
            var key = CrateMatch.ItemKey(r.Crate, r.Artist, r.Title);
            var known = state.Crates.GetValueOrDefault(r.Crate)?.Items.FirstOrDefault(i => i.Key == key);
            if (known is null && !added.Contains(key)) continue;   // not in the list (removed, or never there)
            // The item itself: a corrected artist or title, a note and its link. Not a verdict or a find, so always.
            if (r.FixArtist is not null || r.FixTitle is not null || r.ItemNote is not null || r.Link is not null)
                events.Add(new DiscoverEvent
                {
                    Type = "crate-item", At = default, CrateId = r.Crate, TrackKey = key,
                    Artist = r.FixArtist, Title = r.FixTitle, Note = r.ItemNote, Url = r.Link,
                });
            if (known is not null && !DiscoverState.ResearchMayResolve(known.Resolution)) continue;
            if (state.Tracks.TryGetValue(key, out var heard) && CrateVerdicts.Of(heard) != CrateVerdict.Unheard) continue;
            if (Resolution(r) is { } resolution)
                events.Add(new DiscoverEvent { Type = "crate-resolve", At = default, TrackKey = key, Resolution = resolution });
        }
        events.Add(new DiscoverEvent { Type = "starter", At = default, Name = Version });
        return events;
    }

    /// <summary>
    /// A Bandcamp link plays once its page is read (on Listen); a Beatport-only find
    /// is heard on Beatport. Without either, a search that came to nothing is "not
    /// found" with its words, so Find all does not spend lookups on it again.
    /// </summary>
    public static CrateResolution? Resolution(ResolutionEntry r)
    {
        var bandcamp = Https(r.Bandcamp) is { } bc && bc.Host.EndsWith(".bandcamp.com", StringComparison.OrdinalIgnoreCase) ? r.Bandcamp!.Trim() : null;
        var beatport = CrateMatch.IsBeatportUrl(r.Beatport) ? r.Beatport!.Trim() : null;
        if (bandcamp is null && beatport is null)
            return string.IsNullOrWhiteSpace(r.Note) && string.IsNullOrWhiteSpace(r.ItemNote)
                ? null
                : new CrateResolution { Status = CrateStatus.NotFound, Via = "research", Message = r.Note ?? r.ItemNote, Label = r.Label, ReleaseTitle = r.Release };
        // "confirmed" (Toni or the page itself said so) or "high"; anything else is a search result not yet checked.
        var unsure = r.Confidence is { } c && !c.Equals("high", StringComparison.OrdinalIgnoreCase) && !c.Equals("confirmed", StringComparison.OrdinalIgnoreCase);
        return new CrateResolution
        {
            Status = CrateStatus.Found,
            Source = bandcamp is not null ? "bandcamp" : "beatport",
            PageUrl = bandcamp ?? beatport,
            ReleaseUrl = bandcamp,
            BeatportUrl = beatport,
            Title = r.FixTitle ?? r.Title,
            Artist = r.FixArtist ?? r.Artist,
            Label = r.Label,
            ReleaseTitle = r.Release,
            Released = r.Date,
            Via = "research",
            Free = r.Free == true ? true : null,
            Message = unsure ? "Found by a web search, not checked yet: make sure it is the right track" + (r.Note is { Length: > 0 } n ? $". {n}" : "") : null,
        };
    }

    private static Uri? Https(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;

    public static string Canonical(string url) => DiscoverState.TrackUrlKey(url);

    public static string TitleFromSlug(string trackUrl)
    {
        var slug = Canonical(trackUrl).Split('/').Last();
        return slug.Length == 0 ? trackUrl : char.ToUpperInvariant(slug[0]) + slug[1..].Replace('-', ' ');
    }
}
