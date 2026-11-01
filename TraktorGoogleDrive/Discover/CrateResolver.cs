using TraktorGoogleDrive.Services;

namespace TraktorGoogleDrive.Discover;

public sealed record DiscogsCandidateDto(long Id, string Artist, string Title, string? Year, List<string> Labels,
    string? Catno, List<string> Formats, string? WebUrl, int Score);

public sealed record DiscogsMatchDto(long Id, string Title, string Artists, List<string> Labels, string? Released,
    string? WebUrl, bool TrackFound, string? TrackTitle, List<string> BandcampUrls);

public sealed record DiscogsLookupDto(string Artist, string Title, List<DiscogsCandidateDto> Candidates, bool Ambiguous,
    DiscogsMatchDto? Best);

/// <param name="BandcampUrls">Bandcamp pages in the track's description: the uploader's own buy link.</param>
public sealed record SoundCloudTrackDto(string Url, string Title, string? Author, long? TrackId, List<string>? BandcampUrls = null);

/// <summary>What the resolver may ask. The app answers through the server; tests with recorded answers.</summary>
public interface ICrateLookup
{
    Task<DiscogsLookupDto> DiscogsAsync(string artist, string title, long? releaseId, CancellationToken ct);
    Task<DiscoverApi.ReleasesResponse> BandcampListAsync(string pageUrl, CancellationToken ct);
    Task<DiscoverApi.Release> BandcampReleaseAsync(string releaseUrl, CancellationToken ct);
    Task<SoundCloudTrackDto?> SoundCloudAsync(string? url, string artist, string title, string? uploader, CancellationToken ct);
}

/// <summary>Thrown when a store's limit is spent: "Find all" stops rather than marking the rest not found.</summary>
public sealed class CrateLimitException(string message) : Exception(message);

/// <summary>
/// Finds a playable source for a crate item (ADR 20261025-54, "Crates"), in this
/// order: Discogs by artist and title (release, label, date and any Bandcamp page
/// it lists); Bandcamp by URL only, from Discogs or a checked guess of the
/// artist's or uploader's subdomain, never Bandcamp's search; else not found,
/// with the Discogs release if there was one. SoundCloud is a separate, on-demand
/// step (<see cref="SoundCloudAsync"/>), and so is a URL Toni pastes.
/// </summary>
public sealed class CrateResolver(ICrateLookup lookup)
{
    /// <summary>Release pages read per Bandcamp page tried; each is a request.</summary>
    public const int MaxReleasesPerPage = 3;
    /// <summary>Bandcamp pages tried per item: Discogs' links first, then guesses.</summary>
    public const int MaxPages = 4;

    public async Task<CrateResolution> ResolveAsync(CrateItem item, Action<string> progress, CancellationToken ct, long? discogsRelease = null)
    {
        DiscogsLookupDto? discogs = null;
        string? discogsError = null;
        progress("Searching Discogs…");
        try { discogs = await lookup.DiscogsAsync(item.Artist, item.Title, discogsRelease, ct); }
        catch (DiscoverApiException e) when (e.Status == 429) { throw new CrateLimitException(e.Message); }
        catch (DiscoverApiException e) { discogsError = e.Message; }

        var best = discogs?.Best;
        var pages = new List<(string Url, string Via)>();
        foreach (var u in best?.BandcampUrls ?? []) pages.Add((u, "discogs"));
        // The artist's or uploader's own subdomain: a guess, checked by finding the track on it.
        foreach (var g in CrateMatch.BandcampGuesses(item)) pages.Add((g, "guess"));

        var tried = new List<string>();
        foreach (var (url, via) in pages.DistinctBy(p => p.Url.TrimEnd('/').Replace("/music", ""), StringComparer.OrdinalIgnoreCase).Take(MaxPages))
        {
            ct.ThrowIfCancellationRequested();
            tried.Add(Host(url));
            if (await BandcampAsync(item, url, best?.Title, progress, ct) is { } found)
                return found with { Via = via, DiscogsUrl = best?.WebUrl, DiscogsTitle = Describe(best) };
        }

        if (discogs is { Ambiguous: true, Candidates.Count: > 1 } && discogsRelease is null)
            return new CrateResolution
            {
                Status = CrateStatus.Ambiguous,
                Candidates = discogs.Candidates.Take(5).Select(c => new CrateCandidate("discogs", c.Title, c.Artist,
                    c.Labels.FirstOrDefault(), c.Year, c.WebUrl, DiscogsId: c.Id)).ToList(),
                Message = "Discogs has more than one artist by that name: pick the release",
            };

        return new CrateResolution
        {
            Status = CrateStatus.NotFound,
            Label = best?.Labels.FirstOrDefault(),
            ReleaseTitle = best?.Title,
            Released = best?.Released,
            DiscogsUrl = best?.WebUrl,
            DiscogsTitle = Describe(best),
            Message = (best, discogsError) switch
            {
                (null, { } err) => $"Discogs: {err}. No Bandcamp page had it ({Tried(tried)}).",
                (null, _) => $"Not on Discogs, and no Bandcamp page had it ({Tried(tried)}).",
                ({ BandcampUrls.Count: 0 }, _) => $"On Discogs without a Bandcamp link; no guessed page had it ({Tried(tried)}).",
                _ => $"Not on the Bandcamp pages Discogs lists or guessed ({Tried(tried)}).",
            },
        };
    }

    /// <summary>A Bandcamp or SoundCloud URL Toni pasted (or a label or artist page to look through).</summary>
    public async Task<CrateResolution> FromUrlAsync(CrateItem item, string url, Action<string> progress, CancellationToken ct)
    {
        var text = url.Trim();
        if (text.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase))
        {
            progress("Checking SoundCloud…");
            var sc = await lookup.SoundCloudAsync(text, item.Artist, item.Title, item.Uploader, ct)
                     ?? throw new DiscoverApiException(404, "SoundCloud has no public track at that URL");
            var r = await PreferBandcampAsync(item, sc, "pasted", progress, ct);
            return r.Source == "bandcamp" ? r : r with { Via = "pasted" };
        }
        if (!text.Contains(".bandcamp.com", StringComparison.OrdinalIgnoreCase))
            throw new DiscoverApiException(400, "Paste a Bandcamp or SoundCloud URL");

        var isRelease = text.Contains("/album/", StringComparison.OrdinalIgnoreCase) || text.Contains("/track/", StringComparison.OrdinalIgnoreCase);
        if (isRelease)
        {
            progress("Reading the Bandcamp page…");
            var release = await lookup.BandcampReleaseAsync(text, ct);
            var tracks = release.Tracks;
            // A track page, or a one-track release: that is the track, whatever its title.
            var pick = text.Contains("/track/", StringComparison.OrdinalIgnoreCase)
                ? tracks.FirstOrDefault(t => SameUrl(t.TrackUrl, text)) ?? (tracks.Count == 1 ? tracks[0] : null)
                : tracks.Count == 1 ? tracks[0] : Best(item, tracks);
            if (pick is not null) return Found(release, pick, "pasted");
            return new CrateResolution
            {
                Status = CrateStatus.Ambiguous,
                Message = "Which track on that release?",
                Candidates = tracks.Select(t => new CrateCandidate("bandcamp", t.Title, t.Artist, release.Label, null,
                    t.TrackUrl ?? release.Url, TrackId: t.TrackId, ReleaseUrl: release.Url)).ToList(),
            };
        }
        var onPage = await BandcampAsync(item, text, null, progress, ct);
        return onPage is null
            ? throw new DiscoverApiException(404, "That Bandcamp page has no release with this track")
            : onPage with { Via = "pasted" };
    }

    /// <summary>Toni picked a candidate of an ambiguous result.</summary>
    public async Task<CrateResolution> PickAsync(CrateItem item, CrateCandidate candidate, Action<string> progress, CancellationToken ct)
    {
        if (candidate is { Kind: "bandcamp", TrackId: { } tid, ReleaseUrl: { } releaseUrl })
        {
            var release = await lookup.BandcampReleaseAsync(releaseUrl, ct);
            if (release.Tracks.FirstOrDefault(t => t.TrackId == tid) is { } track) return Found(release, track, "picked");
            throw new DiscoverApiException(404, "That track is no longer on the release");
        }
        if (candidate.DiscogsId is { } id)
        {
            var r = await ResolveAsync(item, progress, ct, id);
            return r.Status == CrateStatus.Found ? r with { Via = "picked" } : r;
        }
        throw new DiscoverApiException(400, "Nothing to pick");
    }

    /// <summary>
    /// On demand only: SoundCloud's oEmbed, for the uploader's and the artist's
    /// permalink under this title. When the track's description links a Bandcamp
    /// page (a buy link), that page is tried first, so the track plays in the bar.
    /// </summary>
    public async Task<CrateResolution?> SoundCloudAsync(CrateItem item, CancellationToken ct, Action<string>? progress = null)
    {
        var sc = await lookup.SoundCloudAsync(null, item.Artist, item.Title, item.Uploader, ct);
        return sc is null ? null : await PreferBandcampAsync(item, sc, "oembed", progress ?? (_ => { }), ct);
    }

    private async Task<CrateResolution> PreferBandcampAsync(CrateItem item, SoundCloudTrackDto sc, string via, Action<string> progress, CancellationToken ct)
    {
        foreach (var url in (sc.BandcampUrls ?? []).Take(2))
            if (await BandcampAsync(item, url, null, progress, ct) is { Status: CrateStatus.Found } onBandcamp)
                return onBandcamp with { Via = "soundcloud-link", Message = $"Linked from its SoundCloud page: {sc.Url}" };
        return FoundOnSoundCloud(sc, via);
    }

    private async Task<CrateResolution?> BandcampAsync(CrateItem item, string url, string? discogsReleaseTitle,
        Action<string> progress, CancellationToken ct)
    {
        try
        {
            if (url.Contains("/album/", StringComparison.OrdinalIgnoreCase) || url.Contains("/track/", StringComparison.OrdinalIgnoreCase))
            {
                progress($"Reading {Host(url)}…");
                var release = await lookup.BandcampReleaseAsync(url, ct);
                return Best(item, release.Tracks) is { } t ? Found(release, t, null) : null;
            }

            progress($"Reading {Host(url)} on Bandcamp…");
            var list = await lookup.BandcampListAsync(url, ct);
            var open = list.Releases.Where(r => CrateMatch.ReleaseWorthOpening(item, r.Title, discogsReleaseTitle)).ToList();
            if (open.Count == 0 && list.Releases.Count <= MaxReleasesPerPage) open = list.Releases;
            foreach (var r in open.Take(MaxReleasesPerPage))
            {
                ct.ThrowIfCancellationRequested();
                progress($"Reading {r.Title} on {Host(url)}…");
                var release = await lookup.BandcampReleaseAsync(r.Url, ct);
                if (Best(item, release.Tracks) is { } t) return Found(release, t, null);
            }
            return null;
        }
        catch (DiscoverApiException e) when (e.Status is 404 or 400 or 502) { return null; }   // a wrong guess, a stub page
        catch (DiscoverApiException e) when (e.Status == 429) { throw new CrateLimitException(e.Message); }
    }

    private static DiscoverApi.Track? Best(CrateItem item, IEnumerable<DiscoverApi.Track> tracks) =>
        tracks.Select(t => (Track: t, Score: CrateMatch.TitleScore(item, t.Title, t.Artist)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Track)
            .FirstOrDefault();

    private static CrateResolution Found(DiscoverApi.Release release, DiscoverApi.Track track, string? via) => new()
    {
        Status = track.Playable ? CrateStatus.Found : CrateStatus.NotFound,
        Source = "bandcamp",
        PageUrl = track.TrackUrl ?? release.Url,
        ReleaseUrl = release.Url,
        TrackId = track.TrackId,
        Title = track.Title,
        Artist = track.Artist,
        Label = release.Label,
        ReleaseTitle = release.Title,
        Released = release.ReleaseDate?.ToString("yyyy-MM-dd"),
        Duration = track.Duration,
        Via = via,
        Message = track.Playable ? null : "On Bandcamp, but not streamable there: open it on Bandcamp",
    };

    private static CrateResolution FoundOnSoundCloud(SoundCloudTrackDto sc, string via) => new()
    {
        Status = CrateStatus.Found,
        Source = "soundcloud",
        PageUrl = sc.Url,
        Title = sc.Title,
        Artist = sc.Author,
        Via = via,
    };

    private static string? Describe(DiscogsMatchDto? best)
    {
        if (best is null) return null;
        var bits = new[] { best.Labels.FirstOrDefault(), best.Released }.Where(b => !string.IsNullOrWhiteSpace(b));
        var tail = string.Join(", ", bits);
        return $"{best.Artists} – {best.Title}{(tail.Length > 0 ? $" ({tail})" : "")}";
    }

    private static bool SameUrl(string? a, string b) =>
        a is not null && DiscoverState.TrackUrlKey(a) == DiscoverState.TrackUrlKey(b);

    private static string Host(string url) =>
        Uri.TryCreate(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url, UriKind.Absolute, out var u)
            ? u.Host : url;

    private static string Tried(List<string> hosts) => hosts.Count == 0 ? "none to try" : string.Join(", ", hosts);
}
