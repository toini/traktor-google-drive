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
    private readonly HttpClient _http;

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
        public int? ErrorStatus { get; set; }
        /// <summary>Release pages read by the current step (at most <see cref="ReleasesPerStep"/>).</summary>
        public int FetchedThisStep { get; set; }
        /// <summary>The last walk stopped at a release on or before the watermark: everything older is heard.</summary>
        public bool StoppedAtWatermark { get; set; }
    }

    public DiscoverService(CollectionService collection, DiscoverStore store, DiscoverApi api, IJSRuntime js, HttpClient http)
    {
        _collection = collection;
        _store = store;
        _api = api;
        _js = js;
        _http = http;
        _store.Changed += () => Changed?.Invoke();
    }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        var collection = await _collection.GetCollectionAsync();
        BuildFromCollection(collection);
        await LoadReleaseCacheAsync();
        await _store.LoadAsync();
        await ApplyStarterAsync();
        foreach (var release in _releases.Values.DistinctBy(r => r.Url)) ResolveUrlTracks(release);
        Changed?.Invoke();
    }

    /// <summary>
    /// The starter list (wwwroot/discover/starter.json), once. Only after Drive
    /// was read: with an unreadable log there is no telling whether it was applied
    /// on another device, and applying it again could undo a change made there.
    /// </summary>
    private async Task ApplyStarterAsync()
    {
        if (_store.Status is not (DiscoverStore.SaveStatus.Saved or DiscoverStore.SaveStatus.Pending)) return;
        string json;
        try { json = await _http.GetStringAsync("discover/starter.json"); }
        catch (HttpRequestException) { return; }
        if (StarterFile.Parse(json) is not { } starter) return;
        foreach (var e in starter.EventsFor(State)) _store.Append(e);
    }

    /// <summary>
    /// Tracks known only by URL (starter list, imports) get their real title and
    /// artist once their release page has been read.
    /// </summary>
    private void ResolveUrlTracks(ReleaseInfo release)
    {
        foreach (var track in release.Tracks.Where(t => t.TrackUrl is not null))
        {
            var key = DiscoverState.UrlTrackKey(track.TrackUrl!);
            if (State.Tracks.TryGetValue(key, out var known) && known.Artist is null)
                _store.Append(new DiscoverEvent
                {
                    Type = "track", At = default, TrackKey = key, Artist = track.Artist, Title = track.Title,
                    ReleaseTitle = release.Title, ReleaseDate = release.Date, Label = release.Label ?? known.Label,
                });
        }
    }

    /// <summary>Reads the release page of a track known only by URL, on Toni's click.</summary>
    public async Task<string?> LookUpAsync(TrackState track)
    {
        var url = track.ReleaseUrl ?? track.TrackUrl;
        if (url is null) return "No URL to look up";
        try
        {
            var release = await _api.GetReleaseAsync(url);
            var info = ToInfo(release, "");
            ResolveUrlTracks(info);
            Changed?.Invoke();
            return State.Tracks.GetValueOrDefault(track.Key)?.Artist is null ? "That track is not on the release page" : null;
        }
        catch (DiscoverApiException e) { return e.Message; }
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

    public static SeedCandidate ManualCandidate(SeedKind kind, string name, string genre) =>
        new(kind, name.Trim(), genre, SeedOrigin.Manual, 0, 0, 0, null, 0, "Added by hand");

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
        progress.Error = null;
        progress.ErrorStatus = null;
        if (string.IsNullOrWhiteSpace(seed.BandcampUrl))
        {
            progress.Error = seed.NotOnBandcamp ? null : NoUrlMessage;
            Changed?.Invoke();
            return;
        }

        progress.Busy = true;
        progress.FetchedThisStep = 0;
        progress.StoppedAtWatermark = false;
        if (_active is { } run) run.CurrentProgress = progress;
        Say(progress, $"Checking {seed.Name}… reading the release list");
        try
        {
            if (force || progress.Refs.Count == 0)
            {
                var url = seed.BandcampUrl;
                var list = await CountedAsync(() => _api.ListReleasesAsync(url, force));
                progress.Refs = list.Releases;
                progress.Listed = list.Releases.Count;
                progress.Walked = 0;
            }
            if (progress.Refs.Count == 0)
            {
                progress.Error = "no releases on that page. Is it the right Bandcamp page?";
                return;
            }

            for (var i = progress.Walked; i < progress.Refs.Count; i++)
            {
                var r = progress.Refs[i];
                progress.Walked = i + 1;
                if (State.IsReleaseHeard(r.Url, r.Title, null, r.Artist)) continue;

                if (!_releases.TryGetValue(r.Url, out var info))
                {
                    if (progress.FetchedThisStep >= step) { progress.Walked = i; break; }
                    Say(progress, $"Checking {seed.Name}… {i + 1}/{progress.Refs.Count} releases");
                    var release = await CountedAsync(() => _api.GetReleaseAsync(r.Url));
                    progress.FetchedThisStep++;
                    info = ToInfo(release, seed.Key);
                    _releases[info.Url] = info;
                    ResolveUrlTracks(info);
                    if (!string.Equals(info.Url, r.Url, StringComparison.OrdinalIgnoreCase)) _releases[r.Url] = info;
                    await SaveReleaseCacheAsync();
                    Changed?.Invoke();
                }

                // The grid is newest first: past the watermark, everything below is older.
                if (DiscoverState.BeforeWatermark(info.Date, seed.Watermark))
                {
                    progress.StoppedAtWatermark = true;
                    progress.Walked = progress.Refs.Count;
                    break;
                }
            }
            progress.RefreshedAt = DateTimeOffset.UtcNow;
            progress.Message = progress.Walked < progress.Refs.Count
                ? $"{progress.Walked} of {progress.Refs.Count} releases read. Load older for more."
                : $"All {progress.Refs.Count} releases read.";
        }
        catch (DiscoverApiException e)
        {
            progress.Error = e.Status == 404 ? $"no Bandcamp page at {HostOf(seed.BandcampUrl)} (404)" : e.Message;
            progress.ErrorStatus = e.Status;
        }
        finally
        {
            progress.Busy = false;
            Changed?.Invoke();
        }
    }

    public bool HasMore(Seed seed) => ProgressOf(seed) is { Refs.Count: > 0 } p && p.Walked < p.Refs.Count;

    // ---------- refresh runs: progress, cost and a result per seed ----------

    /// <summary>The server spaces Bandcamp requests this far apart (PoliteFetcher), for time estimates.</summary>
    public const int SecondsPerRequest = 2;

    /// <summary>The server's daily request cap, when /api/discover/status cannot say.</summary>
    public const int DefaultDailyCap = 500;

    /// <summary>Most requests one seed can cost per refresh: its release list and one step of release pages.</summary>
    public const int MaxRequestsPerSeed = 1 + ReleasesPerStep;

    public const string NoUrlMessage = "no Bandcamp URL, so it is never searched. Set one under Seeds, or mark it “not on Bandcamp”";

    public enum OutcomeKind { Queued, NothingNew, NotOnBandcamp, NotAdded, Error }

    /// <summary>What one seed's refresh (or add) came to, in words, so a refresh is never a silent no-op.</summary>
    public sealed record SeedOutcome(string Name, OutcomeKind Kind, string Text, bool CapSpent = false);

    /// <summary>One "Refresh seeds", "Refresh" or "Add &amp; search": progress while it runs, then a result per seed.</summary>
    public sealed class RefreshRun
    {
        public required string Genre { get; init; }
        public required string Title { get; init; }
        public required int Total { get; init; }
        public int Done { get; set; }
        public string? Current { get; set; }
        public string? Detail { get; set; }
        public SeedProgress? CurrentProgress { get; set; }
        public int Requests { get; set; }
        public bool Running { get; set; } = true;
        public DateTimeOffset StartedAt { get; init; }
        public List<SeedOutcome> Results { get; } = [];
    }

    private readonly Dictionary<string, RefreshRun> _runs = new();
    private RefreshRun? _active;

    /// <summary>The last run in a genre, running or finished in this session.</summary>
    public RefreshRun? RunOf(string genre) => _runs.GetValueOrDefault(genre);

    /// <summary>A run is going: one at a time, since the server serialises Bandcamp requests anyway.</summary>
    public bool Searching => _active is not null;

    /// <summary>An upper bound on the requests the active run still needs, for "about N s left".</summary>
    public static int RequestsLeft(RefreshRun run)
    {
        var later = Math.Max(0, run.Total - run.Done - 1) * MaxRequestsPerSeed;
        var current = run.CurrentProgress is { Busy: true, Refs.Count: > 0 } p
            ? Math.Min(ReleasesPerStep - p.FetchedThisStep, p.Refs.Count - p.Walked)
            : MaxRequestsPerSeed;
        return Math.Max(0, current) + later;
    }

    private void Say(SeedProgress progress, string message)
    {
        progress.Message = message;
        if (_active is { } run) run.Detail = message;
        Changed?.Invoke();
    }

    private async Task<T> CountedAsync<T>(Func<Task<T>> call)
    {
        if (_active is { } run) run.Requests++;
        Changed?.Invoke();
        return await call();
    }

    private async Task<RefreshRun?> RunAsync<T>(string genre, string title, IReadOnlyList<T> items, Func<T, string> name,
        Func<T, Task<SeedOutcome>> step)
    {
        if (_active is not null || items.Count == 0) return null;
        var run = new RefreshRun { Genre = genre, Title = title, Total = items.Count, StartedAt = DateTimeOffset.UtcNow };
        _runs[genre] = run;
        _active = run;
        Changed?.Invoke();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                run.Current = name(items[i]);
                run.Detail = null;
                run.CurrentProgress = null;
                Changed?.Invoke();
                var outcome = await step(items[i]);
                run.Results.Add(outcome);
                run.Done = i + 1;
                if (!outcome.CapSpent) continue;
                foreach (var rest in items.Skip(i + 1))
                    run.Results.Add(new SeedOutcome(name(rest), OutcomeKind.Error, "not checked: today's Bandcamp request cap is spent", true));
                run.Done = items.Count;
                break;
            }
        }
        finally
        {
            run.Running = false;
            run.Current = null;
            run.Detail = null;
            run.CurrentProgress = null;
            _active = null;
            Changed?.Invoke();
        }
        return run;
    }

    /// <summary>"Refresh seeds": every seed of the genre, "eti lisää" first, with a result for each, including the ones it cannot search.</summary>
    public Task RefreshGenreAsync(string genre)
    {
        var seeds = SeedsOf(genre);
        return RunAsync(genre, $"Checking {Plural(seeds.Count, "watched seed")}", seeds, s => s.Name, async s =>
        {
            await RefreshSeedAsync(s);
            return OutcomeFor(s);
        });
    }

    /// <summary>One seed's Refresh or Load older, from its row.</summary>
    public Task RefreshOneAsync(Seed seed, bool force) =>
        RunAsync(seed.Genre, $"Checking {seed.Name}", [seed], s => s.Name, async s =>
        {
            await RefreshSeedAsync(s, force);
            return OutcomeFor(s);
        });

    /// <summary>What a seed's last refresh came to, in words.</summary>
    public SeedOutcome OutcomeFor(Seed seed)
    {
        var p = ProgressOf(seed);
        if (seed.NotOnBandcamp)
            return new(seed.Name, OutcomeKind.NotOnBandcamp, "marked “not on Bandcamp”, so it is skipped" + ViaText(seed));
        if (p.Error is { } error)
            return new(seed.Name, OutcomeKind.Error, error, p.ErrorStatus == 429);

        // By the releases this seed's walk read, not by which seed fetched them first:
        // a label and an artist on the same page share releases.
        var read = p.Refs.Take(p.Walked)
            .Select(r => _releases.TryGetValue(r.Url, out var info) ? info.Url : r.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queued = Queue(seed.Genre).Where(q => read.Contains(q.Release.Url) || q.Release.SeedKey == seed.Key).ToList();
        if (queued.Count > 0)
        {
            var releases = queued.Select(q => q.Release.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return new(seed.Name, OutcomeKind.Queued, $"{Plural(releases, "new release")} queued ({Plural(queued.Count, "track")})");
        }
        var more = HasMore(seed)
            ? $" Read {p.Walked} of {p.Listed} releases; “Load older” under Seeds reads further back."
            : "";
        return seed.Watermark is { } w
            ? new(seed.Name, OutcomeKind.NothingNew, $"no new releases since {w}.{more}")
            : new(seed.Name, OutcomeKind.NothingNew, $"nothing new: every release read is heard or already in your collection.{more}");
    }

    /// <summary>
    /// Adds a suggestion and searches it at once. Its Bandcamp page is checked first
    /// (that check is the release list the search needs anyway): a seed whose page
    /// does not exist is not added, since it could never find anything. With no URL
    /// given, the collection's "Visit …bandcamp.com" comment is used, else the name
    /// is guessed as &lt;slug&gt;.bandcamp.com.
    /// </summary>
    public async Task<SeedOutcome> AddAndSearchAsync(SeedCandidate candidate, string? url = null)
    {
        SeedOutcome? outcome = null;
        await RunAsync(candidate.Genre, $"Adding {candidate.Name}", [candidate], c => c.Name,
            async c => outcome = await AddOneAsync(c, url));
        return outcome ?? new(candidate.Name, OutcomeKind.NotAdded, "another search is still running; try again when it is done");
    }

    /// <summary>"Add all N", after the cost warning: one after another, each checked before it is added.</summary>
    public Task AddAllAndSearchAsync(string genre, IReadOnlyList<SeedCandidate> candidates) =>
        RunAsync(genre, $"Adding {Plural(candidates.Count, "seed")}", candidates, c => c.Name, c => AddOneAsync(c, null));

    /// <summary>"Try &lt;slug&gt;.bandcamp.com" on a watched seed with no URL: kept only if that page exists.</summary>
    public Task GuessSeedUrlAsync(Seed seed) =>
        RunAsync(seed.Genre, $"Looking for {seed.Name} on Bandcamp", [seed], s => s.Name, async s =>
        {
            var url = GuessUrl(s.Name);
            var check = await CheckPageAsync(s.Name, url);
            var p = ProgressOf(s);
            if (check.Error is { } error)
            {
                p.Error = error + ViaText(s);
                p.ErrorStatus = check.CapSpent ? 429 : 404;
                Changed?.Invoke();
                return new SeedOutcome(s.Name, OutcomeKind.Error, p.Error, check.CapSpent);
            }
            SetSeedUrl(s, url);
            var updated = State.Seeds[s.Key];
            UseList(updated, check.Refs!);
            await RefreshSeedAsync(updated);
            return OutcomeFor(updated);
        });

    /// <summary>Toni says the seed has no Bandcamp page: Refresh skips it and says so.</summary>
    public void MarkNotOnBandcamp(Seed seed)
    {
        ProgressOf(seed).Error = null;
        _store.Append(new DiscoverEvent { Type = "seed-url", At = default, SeedKey = seed.Key, NotOnBandcamp = true });
    }

    /// <summary>
    /// Labels Toni already watches in the seed's genre that carry this artist's
    /// tracks in the collection: Obwigszyh's releases come out on Carnivor Records,
    /// so watching Carnivor finds them without a Bandcamp page of his own.
    /// </summary>
    public IReadOnlyList<string> WatchedLabelsOf(SeedKind kind, string name, string genre)
    {
        if (kind != SeedKind.Artist) return [];
        var key = Names.EntityKey(name);
        return _tracks
            .Where(t => t.Label is not null && t.Genres.Contains(genre) && Names.SplitArtists(t.Artist).Any(a => Names.EntityKey(a) == key))
            .Select(t => t.Label!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(l => State.Seeds.ContainsKey(SeedCandidate.SeedKey(SeedKind.Label, l, genre)))
            .ToList();
    }

    private string ViaText(Seed seed) =>
        WatchedLabelsOf(seed.Kind, seed.Name, seed.Genre) is { Count: > 0 } labels
            ? $" (its releases appear via {string.Join(", ", labels)}, which you watch)"
            : "";

    public static string GuessUrl(string name) => $"https://{Names.BandcampSlugGuess(name)}.bandcamp.com";

    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>Suggestions for an empty Listen tab: "eti lisää" first, then the genre's top lists, ones with a known Bandcamp page first.</summary>
    public IReadOnlyList<SeedCandidate> Suggestions(string genre, int count = 6)
    {
        var eti = EtiLisaaCandidates().Where(c => c.Genre == genre);
        var top = TopCandidates(genre, SeedKind.Label).Take(10).Concat(TopCandidates(genre, SeedKind.Artist).Take(10))
            .OrderBy(c => KnownUrlFor(c.Kind, c.Name) is null)
            .ThenByDescending(c => c.Score);
        return eti.Concat(top).Where(c => !State.Seeds.ContainsKey(c.Key)).DistinctBy(c => c.Key).Take(count).ToList();
    }

    /// <summary>Requests used today and the daily cap, from the server; the default cap when it cannot say.</summary>
    public async Task<(int? UsedToday, int DailyCap)> UsageAsync()
    {
        try
        {
            var status = await _api.StatusAsync();
            return (status.Bandcamp.UsedToday, status.Bandcamp.DailyCap);
        }
        catch (Exception e) when (e is DiscoverApiException or JsonException or NotSupportedException)
        {
            return (null, DefaultDailyCap);
        }
    }

    private async Task<SeedOutcome> AddOneAsync(SeedCandidate c, string? url)
    {
        url = string.IsNullOrWhiteSpace(url) ? KnownUrlFor(c.Kind, c.Name) ?? GuessUrl(c.Name) : url.Trim();
        var check = await CheckPageAsync(c.Name, url);
        if (check.Error is { } error)
        {
            var via = WatchedLabelsOf(c.Kind, c.Name, c.Genre) is { Count: > 0 } labels
                ? $" Its releases already appear via {string.Join(", ", labels)}, which you watch."
                : "";
            return new(c.Name, OutcomeKind.NotAdded, $"not added: {error}.{via}", check.CapSpent);
        }

        AddSeed(c, url);
        var seed = State.Seeds[c.Key];
        if (!string.Equals(seed.BandcampUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            // Already watched (another device, or re-added): use the page just checked.
            SetSeedUrl(seed, url);
            seed = State.Seeds[c.Key];
        }
        UseList(seed, check.Refs!);
        await RefreshSeedAsync(seed);
        return OutcomeFor(seed);
    }

    private void UseList(Seed seed, List<DiscoverApi.ReleaseRef> refs)
    {
        var p = ProgressOf(seed);
        p.Refs = refs;
        p.Listed = refs.Count;
        p.Walked = 0;
    }

    /// <summary>Reads a Bandcamp page's release list, which both checks the page exists and starts the walk.</summary>
    private async Task<(List<DiscoverApi.ReleaseRef>? Refs, string? Error, bool CapSpent)> CheckPageAsync(string name, string url)
    {
        var host = HostOf(url);
        if (_active is { } run) run.Detail = $"Checking {name}… looking for {host}";
        Changed?.Invoke();
        try
        {
            var list = await CountedAsync(() => _api.ListReleasesAsync(url, false));
            return list.Releases.Count == 0
                ? (null, $"no releases at {host}. Is it the right Bandcamp page?", false)
                : (list.Releases, null, false);
        }
        catch (DiscoverApiException e) when (e.Status == 404)
        {
            return (null, $"no Bandcamp page at {host}", false);
        }
        catch (DiscoverApiException e)
        {
            return (null, e.Message, e.Status == 429);
        }
    }

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

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
