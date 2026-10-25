using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.JSInterop;

using TraktorGoogleDrive.Discover;
using TraktorGoogleDrive.Models;

using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Track discovery for the Discover view (ADR 20261025-54): seeds from the
/// collection, Bandcamp releases fetched on Toni's action, the Listen queue,
/// triage, watermarks and the shortlist. State lives in <see cref="DiscoverStore"/>.
/// </summary>
public sealed partial class DiscoverService
{
    /// <summary>How many release pages one refresh may fetch for a seed with no watermark (spike S1).</summary>
    public const int ReleasesPerStep = 10;

    private const string ReleaseCacheKey = "discover:releases:v1";

    private readonly CollectionService _collection;
    private readonly DiscoverStore _store;
    private readonly DiscoverApi _api;
    private readonly IJSRuntime _js;

    private Task? _loading;
    private List<CollectionTrack> _tracks = [];
    private Dictionary<string, List<CollectionTrack>> _byPlaylist = new();
    private Dictionary<string, (string Name, string Genre)> _playlists = new();
    private Dictionary<string, string> _knownUrls = new();
    private Dictionary<string, ReleaseInfo> _releases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SeedProgress> _progress = new();

    public OwnedIndex Owned { get; private set; } = new([]);
    public DiscoverState State => _store.State;
    public DiscoverStore Store => _store;
    public event Action? Changed;

    public sealed class SeedProgress
    {
        public bool Busy { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
        public int Listed { get; set; }
        public int Walked { get; set; }
        public List<DiscoverApi.ReleaseRef> Refs { get; set; } = [];
        public DateTimeOffset? RefreshedAt { get; set; }
    }

    public DiscoverService(CollectionService collection, DiscoverStore store, DiscoverApi api, IJSRuntime js)
    {
        _collection = collection;
        _store = store;
        _api = api;
        _js = js;
        _store.Changed += () => Changed?.Invoke();
    }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        var collection = await _collection.GetCollectionAsync();
        BuildFromCollection(collection);
        await LoadReleaseCacheAsync();
        await _store.LoadAsync();
        Changed?.Invoke();
    }

    private void BuildFromCollection(Collection collection)
    {
        // Which genre folders' playlists hold each track, and how often.
        var genreCounts = new Dictionary<string, Dictionary<string, int>>();
        var playlistPaths = new Dictionary<string, List<string>>();
        _playlists = new();
        foreach (var folder in collection.Folders.Where(f => Genres.IsGenre(f.Name)))
        {
            var genre = folder.Name!;
            foreach (var playlist in folder.Playlists)
            {
                _playlists[playlist.Uuid] = (playlist.Name ?? "", genre);
                var paths = playlist.Tracks.Where(t => t is not null).Select(t => t.Path).ToList();
                playlistPaths[playlist.Uuid] = paths;
                foreach (var path in paths)
                {
                    if (!genreCounts.TryGetValue(path, out var counts)) genreCounts[path] = counts = new();
                    counts[genre] = counts.GetValueOrDefault(genre) + 1;
                }
            }
        }

        var byPath = new Dictionary<string, CollectionTrack>();
        foreach (var t in collection.Tracks)
        {
            var genres = genreCounts.TryGetValue(t.Path, out var c)
                ? c.OrderByDescending(kv => kv.Value).ThenBy(kv => Genres.All.ToList().IndexOf(kv.Key)).Select(kv => kv.Key).ToList()
                : [];
            byPath[t.Path] = new CollectionTrack(
                t.Artist ?? "", t.Title ?? "", t.Label, Stars(_collection.RankingOf(t)), _collection.PlayCountOf(t),
                TrackDate.Parse(t.ImportDate), TrackDate.Parse(_collection.LastPlayedOf(t)), t.Comment, genres);
        }
        _tracks = byPath.Values.ToList();
        _byPlaylist = playlistPaths.ToDictionary(kv => kv.Key,
            kv => kv.Value.Where(byPath.ContainsKey).Select(p => byPath[p]).ToList());
        Owned = new OwnedIndex(collection.Tracks.Select(t => (t.Artist ?? "", t.Title ?? "")));
        _knownUrls = KnownBandcampUrls(collection.Tracks);
    }

    public static int Stars(int? ranking) => ranking is { } r ? Math.Clamp((int)Math.Round(r / 51.0), 0, 5) : 0;

    [GeneratedRegex(@"https?://([a-z0-9-]+)\.bandcamp\.com", RegexOptions.IgnoreCase)]
    private static partial Regex BandcampInComment();

    /// <summary>
    /// Bandcamp writes "Visit https://&lt;seller&gt;.bandcamp.com" into its downloads'
    /// comments (33 tracks in the collection): exact URLs for their labels and artists.
    /// </summary>
    private static Dictionary<string, string> KnownBandcampUrls(IEnumerable<Track> tracks)
    {
        var urls = new Dictionary<string, string>();
        foreach (var t in tracks)
        {
            if (t.Comment is null || BandcampInComment().Match(t.Comment) is not { Success: true } m) continue;
            var url = $"https://{m.Groups[1].Value.ToLowerInvariant()}.bandcamp.com";
            if (!string.IsNullOrWhiteSpace(t.Label)) urls.TryAdd("label:" + Names.EntityKey(t.Label), url);
            foreach (var a in Names.SplitArtists(t.Artist)) urls.TryAdd("artist:" + Names.EntityKey(a), url);
        }
        return urls;
    }

    public string? KnownUrlFor(SeedKind kind, string name) =>
        _knownUrls.GetValueOrDefault($"{(kind == SeedKind.Label ? "label" : "artist")}:{Names.EntityKey(name)}");

    // ---------- seeds ----------

    public IReadOnlyList<SeedCandidate> EtiLisaaCandidates() => Filter(SeedFinder.EtiLisaa(_tracks));

    public IReadOnlyList<(string Uuid, string Name)> PlaylistsOf(string genre) =>
        _playlists.Where(p => p.Value.Genre == genre).Select(p => (p.Key, p.Value.Name))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<SeedCandidate> PlaylistCandidates(string uuid, int minStars, bool artists = true, bool labels = true)
    {
        if (!_playlists.TryGetValue(uuid, out var p) || !_byPlaylist.TryGetValue(uuid, out var tracks)) return [];
        return Filter(SeedFinder.FromPlaylist(tracks, p.Genre, p.Name, minStars, artists, labels));
    }

    public IReadOnlyList<SeedCandidate> TopCandidates(string genre, SeedKind kind) =>
        Filter(SeedFinder.TopList(_tracks, genre, kind, DateOnly.FromDateTime(DateTime.UtcNow)));

    private IReadOnlyList<SeedCandidate> Filter(IReadOnlyList<SeedCandidate> list) =>
        list.Where(c => !State.StopNames.Contains(Names.Normalize(c.Name))).ToList();

    public IReadOnlyList<Seed> SeedsOf(string genre) =>
        State.Seeds.Values.Where(s => s.Genre == genre)
            .OrderBy(s => s.Origin).ThenBy(s => s.Kind).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public void AddSeed(SeedCandidate c, string? url = null) =>
        _store.Append(new DiscoverEvent
        {
            Type = "seed-add", At = default, SeedKey = c.Key, Kind = c.Kind, Name = c.Name, Genre = c.Genre,
            Origin = c.Origin, Reason = c.Reason, Url = url ?? KnownUrlFor(c.Kind, c.Name),
        });

    public void AddSeeds(IEnumerable<SeedCandidate> candidates)
    {
        foreach (var c in candidates) AddSeed(c);
    }

    public void AddManualSeed(SeedKind kind, string name, string genre, string? url) =>
        AddSeed(new SeedCandidate(kind, name.Trim(), genre, SeedOrigin.Manual, 0, 0, 0, null, 0, "Added by hand"),
            string.IsNullOrWhiteSpace(url) ? null : url.Trim());

    public void RemoveSeed(Seed seed) =>
        _store.Append(new DiscoverEvent { Type = "seed-remove", At = default, SeedKey = seed.Key });

    public void NeverSuggest(string name) =>
        _store.Append(new DiscoverEvent { Type = "stop-name", At = default, Name = name });

    public void SetSeedUrl(Seed seed, string? url) =>
        _store.Append(new DiscoverEvent { Type = "seed-url", At = default, SeedKey = seed.Key, Url = url?.Trim() });

    /// <summary>Toni editing the watermark by hand: may lower or clear it.</summary>
    public void SetWatermark(Seed seed, string? month) =>
        _store.Append(new DiscoverEvent { Type = "watermark", At = default, SeedKey = seed.Key, Month = month, Clear = true });

    /// <summary>"Heard through" from the queue: only ever raises it.</summary>
    public void RaiseWatermark(Seed seed, string month) =>
        _store.Append(new DiscoverEvent { Type = "watermark", At = default, SeedKey = seed.Key, Month = month });

    /// <summary>
    /// Offered only when this session has read the seed's whole list down to its
    /// watermark: the walk goes newest first, so anything unread is older, and a
    /// watermark above it would hide it unheard.
    /// </summary>
    public string? HeardThroughOffer(Seed seed) =>
        ProgressOf(seed) is { Refs.Count: > 0 } p && p.Walked >= p.Refs.Count
            ? QueueBuilder.HeardThrough(State, seed, _releases.Values, Owned)
            : null;

    // ---------- releases ----------

    public SeedProgress ProgressOf(Seed seed) =>
        _progress.TryGetValue(seed.Key, out var p) ? p : _progress[seed.Key] = new SeedProgress();

    public int ReleaseCount(Seed seed) => _releases.Values.Count(r => r.SeedKey == seed.Key);

    /// <summary>
    /// Fetches a seed's release list, then walks it newest first, fetching release
    /// pages until one is at or before the watermark, or <paramref name="step"/>
    /// new pages were read (spike S1: the list has no dates). One request at a
    /// time; the server spaces them 2 s apart.
    /// </summary>
    public async Task RefreshSeedAsync(Seed seed, bool force = false, int step = ReleasesPerStep)
    {
        var progress = ProgressOf(seed);
        if (progress.Busy) return;
        if (string.IsNullOrWhiteSpace(seed.BandcampUrl))
        {
            progress.Error = "No Bandcamp URL yet";
            Changed?.Invoke();
            return;
        }

        progress.Busy = true;
        progress.Error = null;
        progress.Message = "Reading the release list…";
        Changed?.Invoke();
        try
        {
            if (force || progress.Refs.Count == 0)
            {
                var list = await _api.ListReleasesAsync(seed.BandcampUrl, force);
                progress.Refs = list.Releases;
                progress.Listed = list.Releases.Count;
                progress.Walked = 0;
            }
            if (progress.Refs.Count == 0)
            {
                progress.Error = "No releases on that page. Is it the right Bandcamp page?";
                return;
            }

            var fetched = 0;
            for (var i = progress.Walked; i < progress.Refs.Count; i++)
            {
                var r = progress.Refs[i];
                progress.Walked = i + 1;
                if (State.HeardReleases.Contains(r.Url)) continue;

                if (!_releases.TryGetValue(r.Url, out var info))
                {
                    if (fetched >= step) { progress.Walked = i; break; }
                    progress.Message = $"Reading {r.Title} ({i + 1} of {progress.Refs.Count})…";
                    Changed?.Invoke();
                    var release = await _api.GetReleaseAsync(r.Url);
                    fetched++;
                    info = ToInfo(release, seed.Key);
                    _releases[info.Url] = info;
                    if (!string.Equals(info.Url, r.Url, StringComparison.OrdinalIgnoreCase)) _releases[r.Url] = info;
                    await SaveReleaseCacheAsync();
                    Changed?.Invoke();
                }

                // The grid is newest first: past the watermark, everything below is older.
                if (DiscoverState.BeforeWatermark(info.Date, seed.Watermark)) { progress.Walked = progress.Refs.Count; break; }
            }
            progress.RefreshedAt = DateTimeOffset.UtcNow;
            progress.Message = progress.Walked < progress.Refs.Count
                ? $"{progress.Walked} of {progress.Refs.Count} releases read. Load older for more."
                : $"All {progress.Refs.Count} releases read.";
        }
        catch (DiscoverApiException e)
        {
            progress.Error = e.Message;
        }
        finally
        {
            progress.Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Refreshes every seed of a genre with a URL, "eti lisää" seeds first. Sequential, never concurrent.</summary>
    public async Task RefreshGenreAsync(string genre)
    {
        foreach (var seed in SeedsOf(genre).Where(s => !string.IsNullOrWhiteSpace(s.BandcampUrl)))
            await RefreshSeedAsync(seed);
    }

    public bool HasMore(Seed seed) => ProgressOf(seed) is { Refs.Count: > 0 } p && p.Walked < p.Refs.Count;

    private static ReleaseInfo ToInfo(DiscoverApi.Release r, string seedKey) => new(
        r.Source,
        r.Type == "track" && r.AlbumUrl is not null ? r.AlbumUrl : r.Url,
        r.Title,
        r.Artist,
        r.Label,
        r.ReleaseDate is { } d ? DateOnly.FromDateTime(d.UtcDateTime) : null,
        r.Tags,
        r.Tracks.Select(t => new ReleaseTrack(t.TrackId, t.Number, t.Title, t.Artist, t.Duration, t.TrackUrl, t.Playable)).ToList(),
        seedKey);

    public IReadOnlyList<QueueItem> Queue(string genre) => QueueBuilder.Build(State, genre, _releases.Values, Owned);

    // ---------- triage ----------

    public void Judge(QueueItem item, Pass1? pass1 = null, int? rating = null, Decision? decision = null, string? note = null) =>
        _store.Append(TrackEvent(item, pass1, rating, decision, note));

    public void Judge(TrackState track, int? rating = null, Decision? decision = null, string? note = null, bool? bought = null) =>
        _store.Append(new DiscoverEvent
        {
            Type = "track", At = default, TrackKey = track.Key, Rating = rating, Decision = decision, Note = note, Bought = bought,
        });

    /// <summary>The undo of a mis-tap: the verdict goes, "heard" stays (ADR §4.3), so it does not come back to the queue.</summary>
    public void ClearVerdict(string trackKey) =>
        _store.Append(new DiscoverEvent { Type = "track", At = default, TrackKey = trackKey, Clear = true });

    /// <summary>Skips every queued track of a release, and marks the release heard.</summary>
    public void SkipRelease(QueueItem item, IEnumerable<QueueItem> queue)
    {
        foreach (var other in queue.Where(q => q.Release.Url == item.Release.Url))
            _store.Append(TrackEvent(other, Pass1.Skip, null, null, null));
        _store.Append(new DiscoverEvent { Type = "release-heard", At = default, ReleaseUrl = item.Release.Url });
    }

    private DiscoverEvent TrackEvent(QueueItem item, Pass1? pass1, int? rating, Decision? decision, string? note) => new()
    {
        Type = "track",
        At = default,
        TrackKey = item.TrackKey,
        ReleaseUrl = item.Release.Url,
        Artist = item.Track.Artist,
        Title = item.Track.Title,
        Label = item.Release.Label,
        ReleaseTitle = item.Release.Title,
        ReleaseDate = item.Release.Date,
        Genre = State.Seeds.TryGetValue(item.Release.SeedKey, out var s) ? s.Genre : null,
        Url = item.Track.TrackUrl,
        Pass1 = pass1,
        Rating = rating,
        Decision = decision,
        Note = note,
    };

    // ---------- shortlist ----------

    public IReadOnlyList<TrackState> Shortlist(string genre) =>
        State.Tracks.Values.Where(t => t.Genre == genre && t.Decision == Decision.Buy && !t.Bought)
            .OrderByDescending(t => t.Rating ?? 0).ThenBy(t => t.ReleaseDate).ThenBy(t => t.Artist).ToList();

    public IReadOnlyList<TrackState> Liked(string genre) =>
        State.Tracks.Values.Where(t => t.Genre == genre && t.Pass1 is Pass1.Like or Pass1.Maybe && t.Decision is null)
            .OrderBy(t => t.Pass1).ThenByDescending(t => t.Rating ?? 0).ThenBy(t => t.ReleaseDate).ToList();

    /// <summary>One block per release, release URL first: a Bandcamp cart is filled release by release.</summary>
    public static string ShortlistText(IEnumerable<TrackState> tracks)
    {
        var sb = new StringBuilder();
        foreach (var release in tracks.GroupBy(t => t.ReleaseUrl ?? ""))
        {
            var first = release.First();
            sb.Append(first.Label is null ? "" : first.Label + " — ")
              .Append(first.ReleaseTitle ?? "")
              .Append(first.ReleaseDate is { } d ? $" ({d:yyyy-MM-dd})" : "")
              .Append('\n');
            if (release.Key.Length > 0) sb.Append(release.Key).Append('\n');
            foreach (var t in release)
            {
                sb.Append("  ").Append(t.Artist).Append(" – ").Append(t.Title);
                if (t.Rating is { } r) sb.Append(' ').Append(new string('★', r));
                if (!string.IsNullOrWhiteSpace(t.Note)) sb.Append(" — ").Append(t.Note);
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // ---------- release cache ----------

    private async Task LoadReleaseCacheAsync()
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", ReleaseCacheKey);
            if (string.IsNullOrEmpty(json)) return;
            var list = JsonSerializer.Deserialize<List<ReleaseInfo>>(json, DiscoverState.Json) ?? [];
            _releases = list.ToDictionary(r => r.Url, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* a broken cache only costs refetches */ }
    }

    private async Task SaveReleaseCacheAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_releases.Values.DistinctBy(r => r.Url).ToList(), DiscoverState.Json);
            await _js.InvokeVoidAsync("localStorage.setItem", ReleaseCacheKey, json);
        }
        catch { /* storage full or blocked: the in-memory copy still serves this session */ }
    }
}
