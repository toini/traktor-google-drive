using Microsoft.JSInterop;

using TraktorGoogleDrive.Models;

namespace TraktorGoogleDrive.Services;

public enum PlaybackState
{
    Idle,
    Loading,
    Playing,
    Paused,
    Error,
    Unauthorized,
}

/// <summary>
/// The local track's bytes. <paramref name="State"/> is "stream" (proxy only:
/// too big for the cache, or caching is off), "downloading" (streaming while the
/// whole file downloads; <paramref name="Fraction"/> of it is here) or "cached"
/// (playing from memory, no network needed). <paramref name="Network"/> is ""
/// normally, "buffering" while starved of data, "reconnecting" after a drop.
/// </summary>
public sealed record BufferStatus(string State, double Fraction, bool NextReady, string Network)
{
    public static readonly BufferStatus None = new("none", 0, false, "");
}

/// <summary>
/// Owns the app's single audio element. Components ask this to play; they never
/// hold an audio element themselves, so simultaneous playback cannot happen.
/// While a Cast session is connected it routes to the TV instead, so neither a
/// play button nor the player bar has to know which output it is driving.
///
/// It also owns "what plays next". A playlist page *offers* its rows in table
/// order; the player adopts them as its queue when one of them is played, and
/// keeps that queue when the page goes away, so browsing never stops the music
/// (ADR 20261018-63 §5.1). Continuous play and gig preview advance through it
/// (ADR 20261018-47 §3–4).
///
/// And when another of this account's devices is playing and this tab has
/// nothing of its own loaded, it routes to that device instead
/// (<see cref="IsRemote"/>, ADR 20261018-63): the player bar then shows and
/// controls the other device with the same buttons.
/// </summary>
public class PlayerService : IAsyncDisposable
{
    private const string ContinuousPlayKey = "player:continuous-play";

    /// <summary>Gig preview gives up after this many tracks fail in a row.</summary>
    private const int MaxConsecutiveFailures = 3;

    private readonly IJSRuntime _js;
    private readonly AppErrors _errors;
    private readonly CastService _cast;
    private readonly RemoteService _remote;
    private IJSObjectReference? _module;
    private DotNetObjectReference<PlayerService>? _self;

    private string? _localFileId;
    private PlaybackState _localState = PlaybackState.Idle;
    private double _localTime;
    private double _localDuration;

    private List<FileEntry> _queue = [];
    private string? _queuePlaylist;
    private List<FileEntry> _offer = [];
    private string? _offerPlaylist;
    private int _lastIndex = -1;
    private bool _advancing;
    private int _consecutiveFailures;
    private bool _preferencesLoaded;
    // Whether the loaded track was started at its preview start (load cue) in
    // this preview. If not, a resume from before the load cue jumps to it.
    private bool _previewStartApplied;
    // The row a PlayEntryAsync is still fetching a token for, and a counter that
    // lets the newest of several quick picks win.
    private FileEntry? _requested;
    private int _playGeneration;

    // What is loaded here, as opposed to on another device (the public
    // properties below route between the two).
    private string? _title;
    private FileEntry? _file;
    private bool _isWav;
    private bool _continuous;
    private bool _preview;
    private PreviewRange? _range;

    public PlayerService(IJSRuntime js, AppErrors errors, CastService cast, RemoteService remote)
    {
        _js = js;
        _errors = errors;
        _cast = cast;
        _remote = remote;
        _cast.Changed += OnCastChanged;
        _cast.Progress += OnCastProgress;
        _cast.Finished += OnCastFinished;
        _remote.Changed += OnRemoteChanged;
    }

    private void OnRemoteChanged() => Changed?.Invoke();

    // Subscribers watch this one event, so the TV's transitions have to reach them
    // the same way the local element's do. Connecting also silences this browser,
    // or the set plays in two places at once.
    private void OnCastChanged()
    {
        if (_cast.IsConnected && _localState is PlaybackState.Playing or PlaybackState.Loading)
            _ = StopAsync();

        Changed?.Invoke();
    }

    private void OnCastProgress()
    {
        // The receiver has no "stop at" for a single LOAD, so the preview end on
        // the TV is checked here, on its ~1 s position ticks.
        if (_preview && _cast.IsConnected && _cast.State == PlaybackState.Playing
            && _range?.End is { } end && _cast.CurrentTime >= end)
            _ = AdvanceAsync();

        Changed?.Invoke();
    }

    private void OnCastFinished()
    {
        if (_preview || _continuous) _ = AdvanceAsync();
    }

    /// <summary>
    /// Another of this account's devices is playing and this tab has nothing of
    /// its own loaded, so the bar shows and controls that device.
    /// </summary>
    public bool IsRemote => _remote.RemoteSnapshot is not null && _localFileId is null && !_cast.IsConnected;

    private PlayerSnapshot? Remote => IsRemote ? _remote.RemoteSnapshot : null;

    /// <summary>"Mac · Chrome", the device the bar is controlling.</summary>
    public string? RemoteDeviceName => Remote?.DeviceName;

    /// <summary>The remote player is casting; the TV's name.</summary>
    public string? RemoteCastDeviceName => Remote?.CastDeviceName;

    /// <summary>The remote player refused to start without a click (autoplay policy).</summary>
    public bool RemoteBlocked => Remote?.State == "Blocked";

    /// <summary>The remote player's Google session expired.</summary>
    public bool RemoteNeedsSignIn => Remote?.NeedsSignIn == true;

    /// <summary>The browser refused play() without a user gesture; cleared by the next play.</summary>
    public bool AutoplayBlocked { get; private set; }

    public string? CurrentFileId =>
        Remote is { } r ? r.Track?.FileId : _cast.IsConnected ? _cast.CurrentFileId : _localFileId;

    public PlaybackState State =>
        Remote is { } r
            ? r.State == "Blocked" ? PlaybackState.Paused
            : Enum.TryParse<PlaybackState>(r.State, out var parsed) ? parsed : PlaybackState.Idle
        : _cast.IsConnected ? _cast.State : _localState;

    public double CurrentTime =>
        Remote is not null ? _remote.RemotePosition : _cast.IsConnected ? _cast.CurrentTime : _localTime;

    public double Duration =>
        Remote is { } r ? r.Track?.Duration ?? 0 : _cast.IsConnected ? _cast.Duration : _localDuration;

    /// <summary>Which output is driving, so the player bar can label itself.</summary>
    public bool IsCasting => Remote is null && _cast.IsConnected;
    public string? CastDeviceName => _cast.DeviceName;

    /// <summary>
    /// Describes the loaded media, not the output: the same recording is playing
    /// whether it comes out of this browser or the television.
    /// </summary>
    public string? CurrentTitle => Remote is { } r ? r.Track?.Title : _title;

    /// <summary>
    /// The proxy URL for the loaded file. It carries no token: auth.js keeps the
    /// current one in a cookie, so a set that outlives the ~1h token keeps working.
    /// </summary>
    public string? CurrentUrl { get; private set; }

    /// <summary>
    /// Playback stopped because the Google token ran out and could not be renewed
    /// without the user. The player bar offers a one-click sign-in that resumes at
    /// the same position (or starts the track it was about to start). Local only:
    /// a remote player's sign-in is <see cref="RemoteNeedsSignIn"/>, and only that
    /// device can do it.
    /// </summary>
    public bool NeedsSignIn => !IsRemote && !_cast.IsConnected && _localState == PlaybackState.Unauthorized;

    // The track an advance could not start for want of a token, so a sign-in
    // starts it with its preview range. Null when a 401 stalled the loaded track:
    // player.js holds that position itself.
    private FileEntry? _startAfterSignIn;

    /// <summary>The playlist row that is loaded, for its cues and format. Null for a remote player.</summary>
    public FileEntry? CurrentFile => Remote is not null ? null : _file;

    /// <summary>True when the source is uncompressed, i.e. a waveform can be sampled with range reads.</summary>
    public bool CurrentIsWav => Remote is { } r ? r.Track?.IsWav == true : _isWav;

    /// <summary>Advance to the next row when a track ends. Remembered per browser.</summary>
    public bool ContinuousPlay => Remote is { } r ? r.Continuous : _continuous;

    /// <summary>Gig preview: each track plays from its load cue to its last loop, then the next.</summary>
    public bool PreviewActive => Remote is { } r ? r.PreviewActive : _preview;

    /// <summary>
    /// How the local track is held: streaming, downloading whole, or cached, and
    /// whether the next one is ready offline. Null for a remote player or the TV,
    /// which fetch for themselves.
    /// </summary>
    public BufferStatus? Buffer =>
        Remote is null && !_cast.IsConnected && _localFileId is not null && _file?.External is null ? _buffer : null;

    private BufferStatus _buffer = BufferStatus.None;
    // Whether this tab is playing for real (not just showing a restored track),
    // so a queue change re-plans what to download.
    private bool _prefetching;

    /// <summary>The range the current track plays in gig preview; null outside it.</summary>
    public PreviewRange? CurrentRange =>
        Remote is { } r ? r.Preview is { } p ? new PreviewRange(p.Start, p.End, p.Note) : null : _range;

    /// <summary>1-based position of the current track in the queue, 0 when not in it.</summary>
    public int QueuePosition => Remote is { } r ? r.QueuePosition : _file is null ? 0 : IndexOf(_file) + 1;
    public int QueueCount => Remote is { } r ? r.QueueCount : _queue.Count;
    public bool HasQueue => QueueCount > 0;

    /// <summary>The playlist whose rows the queue holds, if any.</summary>
    public string? QueuePlaylistUuid => _queuePlaylist;

    /// <summary>Raised on every state transition so components can re-render.</summary>
    public event Action? Changed;

    public bool IsPlaying(string fileId) => CurrentFileId == fileId && State == PlaybackState.Playing;
    public bool IsLoading(string fileId) => CurrentFileId == fileId && State == PlaybackState.Loading;

    /// <summary>The loaded track is this row and it is playing. Row-aware: see <see cref="IsCurrentRow"/>.</summary>
    public bool IsPlaying(FileEntry row) => IsCurrentRow(row) && State == PlaybackState.Playing;
    public bool IsLoading(FileEntry row) => IsCurrentRow(row) && State == PlaybackState.Loading;

    /// <summary>
    /// Whether <paramref name="row"/> is the loaded track's row. A playlist can
    /// list the same Drive file twice, so the file id alone is not enough: of
    /// the queue's rows only the one that was played counts. A row outside the
    /// queue (another playlist's page) still counts by its file, as does any row
    /// when the player does not know which row it is on (a remote player, or a
    /// Cast session whose media another sender changed).
    /// </summary>
    public bool IsCurrentRow(FileEntry row)
    {
        if (string.IsNullOrEmpty(row.DriveFileId) || CurrentFileId != row.DriveFileId) return false;
        if (Remote is not null || _file is null || _file.DriveFileId != row.DriveFileId) return true;
        return ReferenceEquals(_file, row) || !_queue.Contains(row);
    }

    private async Task<IJSObjectReference> ModuleAsync()
    {
        if (_module is not null) return _module;
        _module = await _js.InvokeAsync<IJSObjectReference>("import", "./player.js");
        _self = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("init", _self);
        return _module;
    }

    /* ---------- queue and modes ---------- */

    /// <summary>
    /// A playlist page's playable rows, in the order its table shows them. Called
    /// on load and on every re-sort. The player adopts them as its queue when
    /// they are its queue's own playlist (a re-sort), when nothing is loaded that
    /// they would displace, or when one of them is played. Otherwise they wait:
    /// browsing another playlist must not change what plays next.
    /// </summary>
    public void OfferQueue(string playlistUuid, IEnumerable<FileEntry> rowsInTableOrder)
    {
        _offer = rowsInTableOrder.Where(f => !string.IsNullOrEmpty(f.DriveFileId)).ToList();
        _offerPlaylist = playlistUuid;

        var loadedHere = _cast.IsConnected ? _cast.CurrentFileId : _localFileId;
        if (_queuePlaylist == playlistUuid
            || loadedHere is null
            // A track cued by PlaybackMemory has no queue until its playlist loads.
            || (_queue.Count == 0 && _offer.Any(f => f.DriveFileId == loadedHere)))
            AdoptOffer();
        else
            Changed?.Invoke();
    }

    /// <summary>The page went away. The queue it may have given the player stays.</summary>
    public void WithdrawOffer(string playlistUuid)
    {
        if (_offerPlaylist != playlistUuid) return;
        _offer = [];
        _offerPlaylist = null;
    }

    /// <param name="picked">
    /// The row being played, if that is why the offer is adopted: when it is a
    /// copy of the loaded track, the loaded track becomes that row.
    /// </param>
    private void AdoptOffer(FileEntry? picked = null)
    {
        if (_offerPlaylist is null) return;

        var current = _file is null ? -1 : IndexOf(_file);
        if (current >= 0) _lastIndex = current;

        // Gig preview walks one playlist; another one taking over ends it.
        if (_preview && _queuePlaylist is not null && _queuePlaylist != _offerPlaylist)
            EndPreview("another playlist started");

        var samePlaylist = _queuePlaylist == _offerPlaylist;
        _queue = _offer;
        _queuePlaylist = _offerPlaylist;

        var loadedHere = _cast.IsConnected ? _cast.CurrentFileId : _localFileId;
        if (_file is null && loadedHere is not null)
            _file = _queue.FirstOrDefault(f => f.DriveFileId == loadedHere);
        else if (_file is not null && !_queue.Contains(_file))
            _file = picked is not null && picked.DriveFileId == _file.DriveFileId
                ? picked
                : RowFor(_file, samePlaylist ? _file.PlaylistIndex : null) ?? _file;

        // A re-sort moves the current row; remember where it now sits.
        if (_file is not null && IndexOf(_file) is var moved and >= 0) _lastIndex = moved;
        // ...and with it, which track comes next.
        if (_prefetching) _ = PlanPrefetchAsync();
        Changed?.Invoke();
    }

    /* ---------- whole-file download ---------- */

    /// <summary>
    /// Tells player.js which files to download whole: the loaded track, then the
    /// one Next (or continuous play, or gig preview) would start. The NML's size
    /// estimate goes along, so a set too big for the cache is never started.
    /// </summary>
    private async Task PlanPrefetchAsync()
    {
        if (_module is null || _localFileId is null || _cast.IsConnected) return;
        _prefetching = true;
        var loaded = _file is not null && _file.DriveFileId == _localFileId ? _file : null;
        // A store stream (Discover) cannot be downloaded for offline play: its CDN
        // sends no CORS headers. It is planned as stream-only, so prefetch.js leaves it alone.
        var current = new PrefetchItem(_localFileId,
            loaded is not null ? DriveAudio.EstimatedBytes(loaded) : null, null, null, loaded is { CanCache: false });
        // The next track is also primed in a second element, at the place it
        // will start: its load cue in gig preview, the top otherwise. Same
        // arithmetic as PlayEntryAsync, so player.js can tell the primed copy fits.
        // Priming is only a src on the spare element, so a store stream gets it too.
        var next = NextEntry is { } n
            ? new PrefetchItem(n.DriveFileId, DriveAudio.EstimatedBytes(n), UrlFor(n),
                _preview ? GigPreview.RangeFor(n.Track, n.Track?.PlaytimeFloat ?? 0).Start : 0, !n.CanCache)
            : null;
        try { await _module.InvokeVoidAsync("plan", current, next); }
        catch (JSException) { /* playback goes on streaming */ }
    }

    /// <summary>The queue row after the loaded one, or null at the end (or with no queue).</summary>
    private FileEntry? NextEntry
    {
        get
        {
            if (_file is null || _file.DriveFileId != _localFileId) return null;
            var i = IndexOf(_file);
            return i >= 0 && i + 1 < _queue.Count ? _queue[i + 1] : null;
        }
    }

    private sealed record PrefetchItem(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("bytes")] long? Bytes,
        [property: System.Text.Json.Serialization.JsonPropertyName("url")] string? Url,
        [property: System.Text.Json.Serialization.JsonPropertyName("start")] double? Start,
        [property: System.Text.Json.Serialization.JsonPropertyName("stream")] bool StreamOnly = false);

    /// <summary>The audio element's URL for a row: Drive through our proxy, or a store stream's redirect.</summary>
    private static string UrlFor(FileEntry file) => file.External?.StreamUrl ?? DriveAudio.UrlFor(file.DriveFileId);

    [JSInvokable]
    public void OnBufferChanged(string state, double fraction, bool nextReady, string? network)
    {
        _buffer = new BufferStatus(state, Math.Clamp(fraction, 0, 1), nextReady, network ?? "");
        Changed?.Invoke();
    }

    /// <summary>
    /// The queue's row for the loaded track once the page rebuilt its rows (a
    /// reload of the same playlist gives new objects): the one at the same
    /// playlist position, so a track listed twice stays on the copy that was
    /// played, otherwise its first copy. Null when the queue lacks the file.
    /// </summary>
    private FileEntry? RowFor(FileEntry file, int? playlistIndex)
    {
        var copies = _queue.Where(f => f.DriveFileId == file.DriveFileId).ToList();
        return copies.FirstOrDefault(f => f.PlaylistIndex == playlistIndex) ?? copies.FirstOrDefault();
    }

    /// <summary>Playing a row of the offered playlist makes it the queue.</summary>
    private void AdoptOfferFor(FileEntry file)
    {
        if (_offerPlaylist is not null && _offerPlaylist != _queuePlaylist && _offer.Contains(file))
            AdoptOffer(file);
    }

    public async Task LoadPreferencesAsync()
    {
        if (_preferencesLoaded) return;
        _preferencesLoaded = true;
        try { _continuous = await _js.InvokeAsync<string?>("localStorage.getItem", ContinuousPlayKey) == "1"; }
        catch { /* storage unavailable — default off */ }
        Changed?.Invoke();
    }

    public async Task SetContinuousPlayAsync(bool on)
    {
        _continuous = on;
        Changed?.Invoke();
        try { await _js.InvokeVoidAsync("localStorage.setItem", ContinuousPlayKey, on ? "1" : "0"); }
        catch { /* not worth failing over */ }
    }

    /// <summary>
    /// Where "Preview set" would start on the playlist page that is showing: the
    /// loaded track when it is one of its rows (playing, paused, or restored after
    /// a reload), otherwise its first playable row in table order. Null with no
    /// rows offered or queued.
    /// </summary>
    public FileEntry? PreviewStartEntry
    {
        get
        {
            var rows = _offerPlaylist is not null ? _offer : _queue;
            return CurrentEntryIn(rows) ?? rows.FirstOrDefault();
        }
    }

    /// <summary>The row of <paramref name="rows"/> for the loaded track, or null when it is not one of them.</summary>
    private FileEntry? CurrentEntryIn(List<FileEntry> rows)
    {
        if (CurrentFile is null || CurrentFileId != CurrentFile.DriveFileId) return null;
        var i = IndexIn(rows, CurrentFile);
        return i >= 0 ? rows[i] : null;
    }

    /// <summary>
    /// Starts gig preview at <see cref="PreviewStartEntry"/>, from its load cue,
    /// with the showing playlist as the queue. The track already loaded is
    /// restarted at its load cue rather than reloaded.
    /// </summary>
    public async Task StartPreviewAsync()
    {
        if (PreviewStartEntry is not { } start) return;
        if (_offerPlaylist is not null) AdoptOffer();
        _preview = true;
        PreviewStoppedReason = null;
        _consecutiveFailures = 0;
        Changed?.Invoke();
        await PlayEntryAsync(start);
    }

    /// <summary>
    /// Why gig preview ended when the user did not end it, for a few seconds after,
    /// so it never just vanishes from the player bar. Null otherwise.
    /// </summary>
    public string? PreviewStoppedReason { get; private set; }

    private static readonly TimeSpan StoppedReasonShownFor = TimeSpan.FromSeconds(8);
    private int _stoppedReasonGeneration;

    /// <summary>Leaves preview mode (the stop button); the current track keeps playing, unbounded.</summary>
    public void StopPreview()
    {
        PreviewStoppedReason = null;
        LeavePreview();
    }

    /// <summary>Leaves preview mode for a reason the player bar shows briefly.</summary>
    private void EndPreview(string reason)
    {
        Console.WriteLine($"[PlayerService] gig preview stopped: {reason}");
        PreviewStoppedReason = reason;
        var generation = ++_stoppedReasonGeneration;
        LeavePreview();
        _ = Task.Delay(StoppedReasonShownFor).ContinueWith(_ =>
        {
            if (generation != _stoppedReasonGeneration || PreviewStoppedReason is null) return;
            PreviewStoppedReason = null;
            Changed?.Invoke();
        }, TaskScheduler.Default);
    }

    private void LeavePreview()
    {
        _preview = false;
        _range = null;
        if (_module is not null) _ = _module.InvokeVoidAsync("setBounds", null).AsTask();
        Changed?.Invoke();
    }

    public Task NextAsync() => IsRemote ? SendRemoteAsync(RemoteCommandKind.Next) : StepAsync(+1);
    public Task PreviousAsync() => IsRemote ? SendRemoteAsync(RemoteCommandKind.Previous) : StepAsync(-1);

    private async Task StepAsync(int direction)
    {
        if (_queue.Count == 0) return;
        var from = _requested ?? _file;
        var index = from is null ? -1 : IndexOf(from);
        // The current row left the queue (or was never in it): continue from
        // where it last was rather than jumping back to the top.
        if (index < 0) index = direction > 0 ? _lastIndex : _lastIndex + 1;

        var target = index + direction;
        if (target < 0 || target >= _queue.Count)
        {
            // End of the list: stop, leaving the last track loaded and paused.
            if (direction > 0 && _preview)
            {
                EndPreview("end of the playlist");
                await PauseAsync();
            }
            return;
        }
        await PlayEntryAsync(_queue[target]);
    }

    /// <summary>One advance per end-of-track, however many ticks report it.</summary>
    private async Task AdvanceAsync()
    {
        if (_advancing) return;
        _advancing = true;
        try { await NextAsync(); }
        catch (Exception ex) { _errors.Report("Could not start the next track", ex); }
        finally { _advancing = false; }
    }

    private int IndexOf(FileEntry file) => IndexIn(_queue, file);

    private static int IndexIn(List<FileEntry> rows, FileEntry file)
    {
        // By reference first: the same Drive file can sit in a playlist twice.
        var i = rows.IndexOf(file);
        return i >= 0 ? i : rows.FindIndex(f => f.DriveFileId == file.DriveFileId);
    }

    /* ---------- playback ---------- */

    public async Task ToggleAsync(FileEntry file, string token)
    {
        // The row the other device is playing: control it there. Any other row
        // plays here, and this tab takes over as the player (last press wins).
        if (IsRemote && CurrentFileId == file.DriveFileId)
        {
            await SendRemoteAsync(State == PlaybackState.Playing ? RemoteCommandKind.Pause : RemoteCommandKind.Play);
            return;
        }

        AdoptOfferFor(file);

        // `token` is whatever the page held when it rendered, which can be over an
        // hour old by now; every load below reads the current one instead.
        // Another row of the same file (a playlist that lists it twice) is a
        // different pick, not a pause: it restarts from that row, and
        // Next/Previous continue from there.
        var otherRow = _file is not null && _file.DriveFileId == file.DriveFileId && !ReferenceEquals(_file, file)
            && _queue.Contains(file);
        if ((_cast.IsConnected ? _cast.CurrentFileId : _localFileId) != file.DriveFileId || otherRow)
        {
            await PlayEntryAsync(file);
            return;
        }

        if (_cast.IsConnected)
        {
            await _cast.ToggleAsync(file, await CurrentTokenAsync() ?? token);
            return;
        }

        if (NeedsSignIn)
        {
            await SignInToContinueAsync();
            return;
        }

        var module = await ModuleAsync();
        if (_localState is PlaybackState.Playing or PlaybackState.Loading)
        {
            await module.InvokeVoidAsync("pause");
            return;
        }
        await ResumeLocalAsync(module);
    }

    /// <summary>
    /// Resumes the loaded track. In gig preview the end bound is passed again, so
    /// a pause never disarms it; a position before the load cue on a track whose
    /// preview start was never applied (it was cued, or restored at 0) starts
    /// from the load cue instead. Once applied, an earlier position can only be a
    /// deliberate seek, and that is kept, as is a seek past the end (ADR
    /// 20261018-47 §4).
    /// </summary>
    private async Task ResumeLocalAsync(IJSObjectReference module)
    {
        if (_localFileId is null || CurrentUrl is null) return;
        await SyncProxyCookieAsync();
        // A restored track starts downloading when it is played, not on page load.
        await PlanPrefetchAsync();

        if (!_preview || _file is not { } file || file.DriveFileId != _localFileId)
        {
            await module.InvokeVoidAsync("play", _localFileId, CurrentUrl, null, null, false);
            return;
        }

        var range = _range ??= GigPreview.RangeFor(file.Track, _localDuration);
        var position = await LocalPositionAsync(module);
        double? start = null;
        if (!_previewStartApplied && position < range.Start) { start = range.Start; position = range.Start; }
        _previewStartApplied = true;
        // Already past the end means the user seeked there: let it play on.
        double? stop = range.End is { } end && position < end ? end : null;
        Changed?.Invoke();
        await module.InvokeVoidAsync("play", _localFileId, CurrentUrl, start, stop, true);
    }

    private async Task<double> LocalPositionAsync(IJSObjectReference module)
    {
        try
        {
            var p = await module.InvokeAsync<MediaPosition?>("position");
            return p?.Current ?? _localTime;
        }
        catch (Exception) { return _localTime; } // the progress tick is close enough
    }

    private sealed record MediaPosition(
        [property: System.Text.Json.Serialization.JsonPropertyName("current")] double Current);

    /// <summary>
    /// Loads a row and plays it. In gig preview that means from its load cue, with
    /// the end bound armed; otherwise from the top.
    /// </summary>
    private async Task PlayEntryAsync(FileEntry file)
    {
        // Until the token arrives this row is where the next Next/Previous steps
        // from, so quick clicks each move one row instead of several landing on
        // the same one; and a newer pick that arrives meanwhile wins outright.
        var generation = ++_playGeneration;
        _requested = file;
        // Expired between tracks: one silent try before asking the user.
        var token = await CurrentTokenAsync() ?? await RenewTokenAsync();
        if (generation != _playGeneration) return;
        _requested = null;
        if (string.IsNullOrEmpty(token) && _cast.IsConnected)
        {
            _errors.Report("Cannot play the next track — the Google session expired",
                "Sign in again and press play.");
            if (_preview) EndPreview("the Google session expired");
            return;
        }

        if (_cast.IsConnected && !file.CanCast)
        {
            // The TV fetches by itself and has no cookie of ours; a store stream
            // stays on this device (ADR 20261025-54 §2.5).
            _errors.Info("Discover previews play on this device only", "Disconnect the TV to listen here.");
            if (_preview) EndPreview("a Discover preview cannot be cast");
            return;
        }

        var fileId = file.DriveFileId;
        var index = IndexOf(file);
        if (index >= 0) _lastIndex = index;

        _file = file;
        _title = file.Track?.Title ?? Path.GetFileName(file.Track?.Path ?? "");
        CurrentUrl = UrlFor(file);
        _isWav = file.CanWaveform && DriveAudio.IsUncompressed(file);
        _localTime = 0;
        // Traktor's PLAYTIME until the player reports its own; with sliced
        // range responses that can take a while.
        _localDuration = file.Track?.PlaytimeFloat ?? 0;
        _range = _preview ? GigPreview.RangeFor(file.Track, _localDuration) : null;
        _previewStartApplied = _preview;

        var start = _range?.Start ?? 0;

        if (_cast.IsConnected)
        {
            // Always a fresh LOAD, even for the file already on the TV.
            await _cast.LoadAsync(file, token!, start);
            return;
        }

        var module = await ModuleAsync();

        if (string.IsNullOrEmpty(token))
        {
            // Nothing to fetch it with. Stop the old track, show this one, and let
            // the sign-in prompt start it; preview mode and its range stay as they are.
            await module.InvokeVoidAsync("stop");
            _startAfterSignIn = file;
            _localFileId = fileId;
            _localState = PlaybackState.Unauthorized;
            Changed?.Invoke();
            return;
        }

        _startAfterSignIn = null;
        // The same file already loaded (preview starting on the current track, or
        // a playlist listing a file twice) is restarted in place: no new load, so
        // no loadstart would ever clear a Loading state.
        if (_localFileId != fileId) _localState = PlaybackState.Loading;
        _localFileId = fileId;
        // Before play: a file already downloaded then plays from memory, and the
        // waveform finds the download to share rather than starting its own.
        await PlanPrefetchAsync();
        if (generation != _playGeneration) return;
        Changed?.Invoke();
        await SyncProxyCookieAsync();
        if (generation != _playGeneration) return;
        await module.InvokeVoidAsync("play", fileId, CurrentUrl, start, _range?.End, true);
    }

    private async Task<string?> CurrentTokenAsync()
    {
        try { return await _js.InvokeAsync<string?>("authGetToken"); }
        catch (JSException) { return null; }
    }

    /// <summary>
    /// A fresh token without a consent screen, or null. Shared with the Cast
    /// sender through auth.js, which runs one renewal at a time. Outside a click
    /// the browser usually blocks Google's popup, so null is a normal answer.
    /// </summary>
    private async Task<string?> RenewTokenAsync()
    {
        try { return await _js.InvokeAsync<string?>("authRefreshToken"); }
        catch (JSException) { return null; }
    }

    /// <summary>
    /// The proxy reads the token from a cookie; make sure it holds the token
    /// sessionStorage has now. A token stored by a sign-in or a renewal is
    /// written there already, so this only matters for one that arrived another way.
    /// </summary>
    private async Task SyncProxyCookieAsync()
    {
        try { await _js.InvokeAsync<bool>("authSyncProxyCookie"); }
        catch (JSException) { /* stale auth.js: the request 401s and says so */ }
    }

    /// <summary>
    /// The player bar's "Sign in again to continue". Runs inside the click, which
    /// is what lets Google's popup open; with consent already given it closes by
    /// itself. Then picks up exactly where playback stopped.
    /// </summary>
    public async Task SignInToContinueAsync()
    {
        var token = await RenewTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            _errors.Info("Still signed out — playback stays paused",
                "Google sign-in was closed or blocked. Click \"Sign in again to continue\" to retry.");
            Changed?.Invoke();
            return;
        }

        if (_startAfterSignIn is { } next)
        {
            _startAfterSignIn = null;
            await PlayEntryAsync(next);
            return;
        }

        var module = await ModuleAsync();
        await SyncProxyCookieAsync();
        _localState = PlaybackState.Loading;
        Changed?.Invoke();
        if (!await module.InvokeAsync<bool>("resume") && _localFileId is not null && CurrentUrl is not null)
            await module.InvokeVoidAsync("play", _localFileId, CurrentUrl, null, null);
    }

    /// <summary>
    /// Loads a track paused at <paramref name="position"/> without playing it,
    /// always on the local element: a reload brings back the last session's
    /// track, and a TV should not start up on its own. Resume plays it from there.
    /// </summary>
    public async Task CueAsync(string fileId, string? title, bool isWav, double duration, double position)
    {
        if (_localFileId is not null) return;

        // The row, if the page already queued it: next/previous then continue
        // from here, and the waveform gets its cue markers. Otherwise OfferQueue
        // adopts it when the playlist's rows arrive.
        _file = _queue.FirstOrDefault(f => f.DriveFileId == fileId);
        if (_file is not null) _lastIndex = IndexOf(_file);
        _title = title;
        CurrentUrl = DriveAudio.UrlFor(fileId);
        _isWav = isWav;
        _localFileId = fileId;
        _localTime = position;
        _localDuration = duration;
        _localState = PlaybackState.Paused;
        _previewStartApplied = false;
        _requested = null;

        var module = await ModuleAsync();
        Changed?.Invoke();
        await SyncProxyCookieAsync();
        await module.InvokeVoidAsync("cue", fileId, CurrentUrl, position);
    }

    /// <summary>
    /// The underlying media element, so WaveSurfer can attach to it rather than
    /// loading a second copy of a 1-2 GB file.
    /// </summary>
    public async Task<IJSObjectReference?> MediaElementAsync()
    {
        var module = await ModuleAsync();
        return await module.InvokeAsync<IJSObjectReference?>("element");
    }

    /// <summary>Play or pause whatever is loaded, on whichever output owns it.</summary>
    public async Task ResumeAsync()
    {
        if (IsRemote) { await SendRemoteAsync(RemoteCommandKind.Play); return; }
        if (_cast.IsConnected) { await _cast.PlayOrPauseAsync(); return; }
        if (NeedsSignIn) { await SignInToContinueAsync(); return; }
        if (_module is null) return;
        await ResumeLocalAsync(_module);
    }

    public async Task PauseAsync()
    {
        if (IsRemote) { await SendRemoteAsync(RemoteCommandKind.Pause); return; }
        if (_cast.IsConnected) { await _cast.PlayOrPauseAsync(); return; }
        if (_module is null) return;
        await _module.InvokeVoidAsync("pause");
    }

    public async Task SeekAsync(double seconds)
    {
        if (IsRemote) { await SendRemoteAsync(RemoteCommandKind.Seek, seconds); return; }
        if (_cast.IsConnected) { await _cast.SeekAsync(seconds); return; }
        if (_module is null) return;
        await _module.InvokeVoidAsync("seek", seconds);
    }

    private async Task SendRemoteAsync(string kind, double? seconds = null)
    {
        var result = await _remote.SendAsync(kind, seconds);
        if (!result.Ok)
            _errors.Info($"{RemoteDeviceName ?? "The other device"} did not take that", result.Reason);
    }

    /// <summary>
    /// Stops the local element only. A Cast session deliberately survives
    /// navigation — browsing to another playlist must not silence the TV.
    /// </summary>
    public async Task StopAsync()
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("stop");
    }

    [JSInvokable]
    public void OnProgress(double currentTime, double duration)
    {
        _localTime = currentTime;
        // A sliced 206 response can leave duration Infinity until enough is
        // buffered; the track's own PLAYTIME is a better source in that case.
        _localDuration = double.IsFinite(duration) && duration > 0 ? duration : _localDuration;
        Changed?.Invoke();
    }

    [JSInvokable]
    public void OnPlaybackStateChanged(string state, string? fileId)
    {
        // player.js saw the preview's end bound go by. Not a state of its own:
        // the next track's load is what changes state.
        if (state == "previewEnd")
        {
            if (_preview) _ = AdvanceAsync();
            return;
        }

        _localState = state switch
        {
            "loading" => _localState == PlaybackState.Playing ? PlaybackState.Playing : PlaybackState.Loading,
            // canplay also fires after a seek while paused, and for a cued track;
            // neither should turn the play button back into a spinner.
            "ready" => _localState is PlaybackState.Playing or PlaybackState.Paused ? _localState : PlaybackState.Loading,
            "playing" => PlaybackState.Playing,
            "paused" => PlaybackState.Paused,
            // Keep the track loaded and paused rather than clearing it: a set
            // that just finished is exactly the one you want to scrub back into.
            "ended" => PlaybackState.Paused,
            "unauthorized" => PlaybackState.Unauthorized,
            "error" or "blocked" => PlaybackState.Error,
            _ => PlaybackState.Idle,
        };

        if (state == "ended") _localTime = 0;
        else if (state == "idle") { _localFileId = null; _localTime = 0; _prefetching = false; }
        else if (fileId is not null) _localFileId = fileId;

        if (state == "playing") _consecutiveFailures = 0;
        if (state is "playing" or "idle") AutoplayBlocked = false;
        if (state == "blocked") AutoplayBlocked = true;

        switch (state)
        {
            case "unauthorized":
                // player.js already tried one silent renewal and holds the position.
                // Not an error to skip past, and not a reason to leave preview mode:
                // the player bar's sign-in button continues from right here.
                _startAfterSignIn = null;
                break;
            case "error" when _preview:
                if (++_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    EndPreview($"{MaxConsecutiveFailures} tracks in a row failed");
                    _errors.Report("Gig preview stopped",
                        $"{MaxConsecutiveFailures} tracks in a row failed to play. Last: {_title} (fileId {fileId}).");
                }
                else
                {
                    _errors.Info($"Skipped \"{_title}\" — it would not play", $"fileId {fileId}.");
                    _ = AdvanceAsync();
                }
                break;
            case "error":
                _errors.Report("Playback failed",
                    $"fileId {fileId}. The browser could not decode or fetch the audio.");
                break;
            case "blocked":
                _errors.Report("Playback blocked by the browser",
                    "Autoplay was refused. Click play again — browsers require a direct user gesture.");
                break;
            case "ended" when _preview || _continuous:
                _ = AdvanceAsync();
                break;
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cast.Changed -= OnCastChanged;
        _cast.Progress -= OnCastProgress;
        _cast.Finished -= OnCastFinished;
        _remote.Changed -= OnRemoteChanged;

        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // Circuit already gone during teardown — nothing to clean up.
            }
        }
        _self?.Dispose();
        GC.SuppressFinalize(this);
    }
}
