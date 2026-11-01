using System.Text.RegularExpressions;

using TraktorGoogleDrive.Discover;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Crates (ADR 20261025-54, "Crates"): named lists of tracks inside a genre, kept
/// apart from the genre's seed queue, stored as events in the same Discover log.
/// This finds each item's playable source on Toni's action, one item at a time,
/// and holds the triage of what it found.
/// </summary>
public sealed partial class CrateService
{
    private readonly DiscoverService _discover;
    private readonly CrateResolver _resolver;
    private readonly Dictionary<string, ItemProgress> _progress = new(StringComparer.Ordinal);

    public event Action? Changed;

    public DiscoverFeatures Features { get; }

    public CrateService(DiscoverService discover, ICrateLookup lookup, DiscoverFeatures features)
    {
        _discover = discover;
        Features = features;
        _resolver = new CrateResolver(lookup);
        _discover.Changed += () => Changed?.Invoke();
    }

    public sealed class ItemProgress
    {
        public bool Busy { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>"Find all": sequential, with a count and the item it is on, stoppable.</summary>
    public sealed class FindAllRun
    {
        public required string CrateId { get; init; }
        public int Total { get; set; }
        public int Done { get; set; }
        public string? Current { get; set; }
        public string? Detail { get; set; }
        public string? Message { get; set; }
        public bool Running { get; set; }
        /// <summary>Calls to the server so far; each costs one to five store requests.</summary>
        public int Lookups { get; set; }
        public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
        public List<FindOutcome> Results { get; } = [];
        public CancellationTokenSource Cancel { get; } = new();
    }

    /// <param name="Kind">found, ambiguous, notfound, owned, error.</param>
    public sealed record FindOutcome(string Name, string Kind, string Text);

    /// <summary>
    /// Upper bound per track for "about N min left": a Discogs lookup (up to 5
    /// requests, 2.6 s apart) and up to 4 Bandcamp pages plus 3 releases (2 s apart),
    /// though most tracks need far fewer.
    /// </summary>
    public const int WorstSecondsPerTrack = 30;

    public FindAllRun? Run { get; private set; }

    public DiscoverState State => _discover.State;
    public DiscoverStore Store => _discover.Store;
    public async Task EnsureLoadedAsync()
    {
        await Features.EnsureLoadedAsync();
        await _discover.EnsureLoadedAsync();
    }

    public IReadOnlyList<Crate> CratesOf(string genre) => State.CratesOf(genre).ToList();
    public Crate? Get(string id) => State.Crates.GetValueOrDefault(id);

    public ItemProgress ProgressOf(CrateItem item) =>
        _progress.TryGetValue(item.Key, out var p) ? p : _progress[item.Key] = new ItemProgress();

    public bool Owns(CrateItem item) => _discover.Owned.Owns(item.Artist, CrateMatch.SearchTitle(item.Title));

    /// <summary>A SoundCloud find while SoundCloud is off counts as not found: kept in the log, not shown.</summary>
    public CrateItemState StateOf(CrateItem item) =>
        Owns(item) ? CrateItemState.Owned
        : item.Resolution?.Status switch
        {
            CrateStatus.Found when item.Resolution.Source == "soundcloud" && !Features.SoundCloud => CrateItemState.NotFound,
            CrateStatus.Found => CrateItemState.Found,
            CrateStatus.Ambiguous => CrateItemState.Ambiguous,
            CrateStatus.NotFound => CrateItemState.NotFound,
            _ => CrateItemState.Pending,
        };

    // ---------- crates ----------

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public string Create(string genre, string name, string? note = null)
    {
        var clean = Spaces().Replace(name.Trim(), " ");
        var id = $"{CrateMatch.Slug(clean)}-{Guid.NewGuid().ToString("N")[..6]}";
        if (id.StartsWith('-')) id = "list" + id;
        Store.Append(new DiscoverEvent { Type = "crate-add", At = default, CrateId = id, Name = clean, Genre = genre, Note = note });
        return id;
    }

    public void Rename(Crate crate, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Store.Append(new DiscoverEvent { Type = "crate-rename", At = default, CrateId = crate.Id, Name = Spaces().Replace(name.Trim(), " ") });
    }

    public void Delete(Crate crate) =>
        Store.Append(new DiscoverEvent { Type = "crate-remove", At = default, CrateId = crate.Id });

    [GeneratedRegex(@"^\s*(?<artist>.+?)\s+[-–—]\s+(?<title>.+?)\s*$")]
    private static partial Regex ArtistDashTitle();

    /// <summary>"Artist - Title", one per line. Returns how many were added; lines without a dash are skipped.</summary>
    public (int Added, int Skipped) AddItems(Crate crate, string text, string? note = null)
    {
        int added = 0, skipped = 0;
        var pos = crate.Items.Count == 0 ? 0 : crate.Items.Max(i => i.Pos) + 1;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ArtistDashTitle().Match(line) is not { Success: true } m) { skipped++; continue; }
            var key = CrateMatch.ItemKey(crate.Id, m.Groups["artist"].Value, m.Groups["title"].Value);
            if (crate.Items.Any(i => i.Key == key)) { skipped++; continue; }
            Store.Append(new DiscoverEvent
            {
                Type = "crate-item", At = default, CrateId = crate.Id, TrackKey = key,
                Artist = m.Groups["artist"].Value, Title = m.Groups["title"].Value, Note = note, Pos = pos++,
            });
            added++;
        }
        return (added, skipped);
    }

    public void RemoveItem(CrateItem item) =>
        Store.Append(new DiscoverEvent { Type = "crate-item-remove", At = default, CrateId = item.CrateId, TrackKey = item.Key });

    // ---------- finding ----------

    private void Save(CrateItem item, CrateResolution? resolution) =>
        Store.Append(new DiscoverEvent
        {
            Type = "crate-resolve", At = default, TrackKey = item.Key, Resolution = resolution, Clear = resolution is null ? true : null,
        });

    public void ClearResolution(CrateItem item) => Save(item, null);

    private async Task<bool> RunAsync(CrateItem item, Func<Action<string>, CancellationToken, Task<CrateResolution?>> work,
        CancellationToken ct = default, string? notFoundMessage = null)
    {
        var p = ProgressOf(item);
        if (p.Busy) return false;
        p.Busy = true;
        p.Error = null;
        p.Message = "Starting…";
        Changed?.Invoke();
        try
        {
            var result = await work(m => { p.Message = m; Changed?.Invoke(); }, ct);
            if (result is null) p.Error = notFoundMessage ?? "Nothing found";
            else Save(item, result);
            return true;
        }
        catch (CrateLimitException e) { p.Error = e.Message; throw; }
        catch (DiscoverApiException e) { p.Error = e.Message; return true; }
        catch (OperationCanceledException) { p.Error = "Stopped"; return false; }
        finally
        {
            p.Busy = false;
            p.Message = null;
            Changed?.Invoke();
        }
    }

    public async Task FindAsync(CrateItem item)
    {
        try { await RunAsync(item, (progress, ct) => _resolver.ResolveAsync(item, progress, ct)!); }
        catch (CrateLimitException) { /* shown on the row */ }
    }

    /// <summary>What the paste box takes: Bandcamp and Beatport, and SoundCloud only with its flag on.</summary>
    public string PasteHint => Features.SoundCloud ? "Paste a Bandcamp, Beatport or SoundCloud URL" : "Paste a Bandcamp or Beatport URL";

    public async Task PasteAsync(CrateItem item, string url)
    {
        if (string.IsNullOrWhiteSpace(url)) { ProgressOf(item).Error = PasteHint + " first"; Changed?.Invoke(); return; }
        if (!Features.SoundCloud && url.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase))
        {
            ProgressOf(item).Error = PasteHint;
            Changed?.Invoke();
            return;
        }
        var beatport = item.Resolution?.BeatportUrl;
        try
        {
            await RunAsync(item, async (progress, ct) =>
            {
                var r = await _resolver.FromUrlAsync(item, url, progress, ct, Features.SoundCloud);
                // A Bandcamp page pasted onto a Beatport find keeps the Beatport link beside it.
                return r.BeatportUrl is null && beatport is not null && r.Status == CrateStatus.Found ? r with { BeatportUrl = beatport } : r;
            });
        }
        catch (CrateLimitException) { }
    }

    /// <summary>
    /// A Bandcamp link known without its track id (a shipped research result):
    /// read that one page, by its URL, so the track plays in the player bar. Its
    /// Beatport link and its origin stay.
    /// </summary>
    public static bool NeedsReading(CrateItem item) =>
        item.Resolution is { Status: CrateStatus.Found, Source: "bandcamp", TrackId: null, PageUrl: not null };

    public async Task<bool> CompleteAsync(CrateItem item)
    {
        if (!NeedsReading(item)) return true;
        var known = item.Resolution!;
        try
        {
            await RunAsync(item, async (progress, ct) =>
            {
                var r = await _resolver.FromUrlAsync(item, known.PageUrl!, progress, ct, soundCloud: false);
                return r with { Via = known.Via, BeatportUrl = r.BeatportUrl ?? known.BeatportUrl, Message = r.Message ?? known.Message };
            });
        }
        catch (CrateLimitException) { }
        return !NeedsReading(item) && StateOf(item) == CrateItemState.Found;
    }

    public async Task PickAsync(CrateItem item, CrateCandidate candidate)
    {
        try { await RunAsync(item, (progress, ct) => _resolver.PickAsync(item, candidate, progress, ct)!); }
        catch (CrateLimitException) { }
    }

    /// <summary>The SoundCloud fallback, on Toni's click for this one item.</summary>
    public async Task TrySoundCloudAsync(CrateItem item)
    {
        if (!Features.SoundCloud) return;
        try
        {
            await RunAsync(item, async (progress, ct) =>
            {
                progress("Asking SoundCloud's oEmbed for the uploader's track…");
                return await _resolver.SoundCloudAsync(item, ct, progress);
            }, notFoundMessage: "Not on SoundCloud under the uploader's or the artist's name with this title. Paste its URL.");
        }
        catch (CrateLimitException) { /* shown on the row */ }
    }

    /// <summary>
    /// Looks up every item not yet looked up, one at a time (the server spaces
    /// the store requests). Owned tracks are skipped without a request. Says so
    /// when there is nothing to do, never a silent no-op.
    /// </summary>
    public async Task FindAllAsync(Crate crate)
    {
        if (Run is { Running: true }) return;
        var todo = crate.Items.Where(i => StateOf(i) == CrateItemState.Pending).ToList();
        var run = Run = new FindAllRun { CrateId = crate.Id, Total = todo.Count, Running = todo.Count > 0 };
        if (todo.Count == 0)
        {
            var owned = crate.Items.Count(i => StateOf(i) == CrateItemState.Owned);
            run.Message = $"Nothing left to look up: all {crate.Items.Count} tracks have a result"
                          + (owned > 0 ? $" ({owned} already in your collection)" : "") + ". Use Find again on a row to retry it.";
            Changed?.Invoke();
            return;
        }

        Changed?.Invoke();
        try
        {
            foreach (var item in todo)
            {
                if (run.Cancel.IsCancellationRequested) break;
                run.Current = $"{item.Artist} – {item.Title}";
                run.Detail = null;
                Changed?.Invoke();
                await RunAsync(item, (progress, ct) =>
                {
                    return _resolver.ResolveAsync(item, m => { run.Detail = m; run.Lookups++; progress(m); }, ct)!;
                }, run.Cancel.Token);
                run.Done++;
                run.Results.Add(OutcomeOf(item));
            }
            run.Message = run.Cancel.IsCancellationRequested
                ? $"Stopped after {run.Done} of {run.Total}."
                : $"Looked up {run.Done} of {run.Total}.";
        }
        catch (CrateLimitException e)
        {
            run.Message = $"Stopped after {run.Done} of {run.Total}: {e.Message}";
        }
        finally
        {
            run.Running = false;
            run.Current = null;
            Changed?.Invoke();
        }
    }

    public void StopFindAll() => Run?.Cancel.Cancel();

    /// <summary>One line of the "Find all" report, in words.</summary>
    public FindOutcome OutcomeOf(CrateItem item)
    {
        var name = $"{item.Artist} – {item.Title}";
        var r = item.Resolution;
        if (ProgressOf(item).Error is { } error && r is null) return new(name, "error", error);
        return StateOf(item) switch
        {
            CrateItemState.Owned => new(name, "owned", "already in your collection"),
            CrateItemState.Found when r!.Source == "soundcloud" => new(name, "found", "on SoundCloud (plays in its widget)"),
            CrateItemState.Found when r!.Source == "beatport" => new(name, "found", "on Beatport"),
            CrateItemState.Found => new(name, "found",
                $"on Bandcamp: {string.Join(" – ", new[] { r!.Label, r.ReleaseTitle }.Where(x => !string.IsNullOrWhiteSpace(x)))}"
                + (r.Via == "guess" ? " (checked guess)" : "")),
            CrateItemState.Ambiguous => new(name, "ambiguous", r!.Message ?? "more than one match: pick one"),
            CrateItemState.NotFound => new(name, "notfound", "not found yet"),
            _ => new(name, "error", "not looked up"),
        };
    }

    // ---------- listening ----------

    /// <summary>What Toni made of an item by ear; Unheard until a verdict, and again after Undo.</summary>
    public CrateVerdict VerdictOf(CrateItem item) => CrateVerdicts.Of(State.Tracks.GetValueOrDefault(item.Key));

    public TrackState? TrackOf(CrateItem item) => State.Tracks.GetValueOrDefault(item.Key);

    public bool Matches(CrateItem item, CrateFilter filter) => CrateVerdicts.Matches(filter, StateOf(item), VerdictOf(item));

    /// <summary>Found, not owned and without a verdict, in list order. Never mixed with the genre's seed queue.</summary>
    public IReadOnlyList<CrateItem> Queue(Crate crate) =>
        crate.Items.Where(i => StateOf(i) == CrateItemState.Found && VerdictOf(i) == CrateVerdict.Unheard).ToList();

    public void Judge(CrateItem item, Pass1? pass1 = null, int? rating = null, Decision? decision = null, string? note = null, bool? bought = null)
    {
        var r = item.Resolution;
        Store.Append(new DiscoverEvent
        {
            Type = "track", At = default, TrackKey = item.Key,
            Artist = r?.Artist ?? item.Artist, Title = r?.Title ?? item.Title,
            Url = r?.PageUrl, ReleaseUrl = r?.ReleaseUrl ?? r?.PageUrl ?? r?.BeatportUrl, Label = r?.Label, ReleaseTitle = r?.ReleaseTitle,
            ReleaseDate = DateOnly.TryParse(r?.Released, out var d) ? d : null,
            // No genre: crate tracks stay out of the genre's own Shortlist.
            Pass1 = pass1, Rating = rating, Decision = decision, Note = note, Bought = bought,
        });
    }

    public void Judge(TrackState track, int? rating = null, Decision? decision = null, bool? bought = null) =>
        Store.Append(new DiscoverEvent { Type = "track", At = default, TrackKey = track.Key, Rating = rating, Decision = decision, Bought = bought });

    /// <summary>Undo: back to Unheard, so it is in "To hear" again (and off the buy list).</summary>
    public void ClearVerdict(CrateItem item) =>
        Store.Append(new DiscoverEvent { Type = "track", At = default, TrackKey = item.Key, Clear = true, Bought = false });

    private IEnumerable<TrackState> TracksOf(Crate crate) =>
        State.Tracks.Values.Where(t => CrateMatch.CrateOf(t.Key) == crate.Id);

    public IReadOnlyList<TrackState> Liked(Crate crate) =>
        TracksOf(crate).Where(t => t.Pass1 is Pass1.Like or Pass1.Maybe && t.Decision is null)
            .OrderBy(t => t.Pass1).ThenByDescending(t => t.Rating ?? 0).ToList();

    public IReadOnlyList<TrackState> Shortlist(Crate crate) =>
        TracksOf(crate).Where(t => t.Decision == Decision.Buy && !t.Bought)
            .OrderByDescending(t => t.Rating ?? 0).ThenBy(t => t.Artist).ToList();
}
