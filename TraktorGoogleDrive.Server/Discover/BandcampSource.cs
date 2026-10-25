using System.Collections.Concurrent;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>
/// Bandcamp, fetched only when Toni acts (ADR 20261025-54, decision 1): the
/// endpoints call this when he opens or refreshes a seed, or when a track is about
/// to play. Nothing here runs on a timer. Pages are cached in memory (a restart
/// costs a few refetches), so a second look within the cache window is free.
/// </summary>
public sealed class BandcampSource
{
    /// <summary>A label's release list barely changes within a day.</summary>
    public static readonly TimeSpan MusicPageTtl = TimeSpan.FromHours(24);

    /// <summary>Release metadata. Its stream URLs expire sooner; see <see cref="StreamMargin"/>.</summary>
    public static readonly TimeSpan ReleaseTtl = TimeSpan.FromDays(7);

    /// <summary>A stream URL is used only while it has at least this long left.</summary>
    public static readonly TimeSpan StreamMargin = TimeSpan.FromHours(1);

    private const int MaxCached = 2000;

    private readonly PoliteFetcher _fetcher;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<BandcampReleaseRef> Items, DateTimeOffset At)> _music = new();
    private readonly ConcurrentDictionary<string, (BandcampRelease Release, DateTimeOffset At)> _releases = new();

    public BandcampSource(PoliteFetcher fetcher, TimeProvider clock)
    {
        _fetcher = fetcher;
        _clock = clock;
    }

    public PoliteFetcher Fetcher => _fetcher;

    public async Task<IReadOnlyList<BandcampReleaseRef>> ListReleasesAsync(Uri musicPage, bool refresh, CancellationToken ct)
    {
        var key = musicPage.ToString();
        var now = _clock.GetUtcNow();
        if (!refresh && _music.TryGetValue(key, out var hit) && now - hit.At < MusicPageTtl) return hit.Items;

        var html = await _fetcher.GetStringAsync(musicPage, ct);
        var items = BandcampParser.ParseMusicPage(html, musicPage);
        Trim(_music);
        _music[key] = (items, now);
        return items;
    }

    /// <param name="needStreams">True when the caller is about to play: a cached page whose stream URLs are near expiry is fetched again.</param>
    public async Task<BandcampRelease?> GetReleaseAsync(Uri releasePage, bool needStreams, CancellationToken ct)
    {
        var key = releasePage.ToString();
        var now = _clock.GetUtcNow();
        if (_releases.TryGetValue(key, out var hit) && now - hit.At < ReleaseTtl
            && (!needStreams || StreamsFresh(hit.Release, now)))
            return hit.Release;

        var html = await _fetcher.GetStringAsync(releasePage, ct);
        var release = BandcampParser.ParseRelease(html, releasePage);
        if (release is null) return null;
        Trim(_releases);
        _releases[key] = (release, now);
        return release;
    }

    /// <summary>The current stream URL of one track, or null if the release has no playable stream for it.</summary>
    public async Task<string?> StreamUrlAsync(Uri releasePage, long trackId, CancellationToken ct)
    {
        var release = await GetReleaseAsync(releasePage, needStreams: true, ct);
        var track = release?.Tracks.FirstOrDefault(t => t.TrackId == trackId);
        return track?.StreamUrl is { } url && BandcampUrls.IsStreamUrl(url) ? url : null;
    }

    private bool StreamsFresh(BandcampRelease release, DateTimeOffset now) =>
        release.Tracks.Where(t => t.StreamUrl is not null)
            .All(t => t.StreamExpires is { } exp && exp - now > StreamMargin);

    private static void Trim<T>(ConcurrentDictionary<string, T> cache)
    {
        if (cache.Count < MaxCached) return;
        // Crude but bounded: one user never gets near this; a runaway client cannot grow memory.
        foreach (var key in cache.Keys.Take(MaxCached / 4)) cache.TryRemove(key, out _);
    }
}
