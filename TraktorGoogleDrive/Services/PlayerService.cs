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
/// Owns the app's single audio element. Components ask this to play; they never
/// hold an audio element themselves, so simultaneous playback cannot happen.
/// While a Cast session is connected it routes to the TV instead, so neither a
/// play button nor the player bar has to know which output it is driving.
///
/// It also owns "what plays next": the playlist page hands it the rows in the
/// order the table shows them, and continuous play and gig preview both advance
/// through that queue (ADR 20261018-47 §3–4).
/// </summary>
public class PlayerService : IAsyncDisposable
{
    private const string ContinuousPlayKey = "player:continuous-play";

    /// <summary>Gig preview gives up after this many tracks fail in a row.</summary>
    private const int MaxConsecutiveFailures = 3;

    private readonly IJSRuntime _js;
    private readonly AppErrors _errors;
    private readonly CastService _cast;
    private IJSObjectReference? _module;
    private DotNetObjectReference<PlayerService>? _self;

    private string? _localFileId;
    private PlaybackState _localState = PlaybackState.Idle;
    private double _localTime;
    private double _localDuration;

    private List<FileEntry> _queue = [];
    private int _lastIndex = -1;
    private bool _advancing;
    private int _consecutiveFailures;
    private bool _preferencesLoaded;

    public PlayerService(IJSRuntime js, AppErrors errors, CastService cast)
    {
        _js = js;
        _errors = errors;
        _cast = cast;
        _cast.Changed += OnCastChanged;
        _cast.Progress += OnCastProgress;
        _cast.Finished += OnCastFinished;
    }

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
        if (PreviewActive && _cast.IsConnected && _cast.State == PlaybackState.Playing
            && CurrentRange?.End is { } end && _cast.CurrentTime >= end)
            _ = AdvanceAsync();

        Changed?.Invoke();
    }

    private void OnCastFinished()
    {
        if (PreviewActive || ContinuousPlay) _ = AdvanceAsync();
    }

    public string? CurrentFileId => _cast.IsConnected ? _cast.CurrentFileId : _localFileId;
    public PlaybackState State => _cast.IsConnected ? _cast.State : _localState;
    public double CurrentTime => _cast.IsConnected ? _cast.CurrentTime : _localTime;
    public double Duration => _cast.IsConnected ? _cast.Duration : _localDuration;

    /// <summary>Which output is driving, so the player bar can label itself.</summary>
    public bool IsCasting => _cast.IsConnected;
    public string? CastDeviceName => _cast.DeviceName;

    /// <summary>
    /// Describes the loaded media, not the output: the same recording is playing
    /// whether it comes out of this browser or the television.
    /// </summary>
    public string? CurrentTitle { get; private set; }

    /// <summary>
    /// The proxy URL for the loaded file. It carries no token: auth.js keeps the
    /// current one in a cookie, so a set that outlives the ~1h token keeps working.
    /// </summary>
    public string? CurrentUrl { get; private set; }

    /// <summary>
    /// Playback stopped because the Google token ran out and could not be renewed
    /// without the user. The player bar offers a one-click sign-in that resumes at
    /// the same position (or starts the track it was about to start).
    /// </summary>
    public bool NeedsSignIn => !_cast.IsConnected && _localState == PlaybackState.Unauthorized;

    // The track an advance could not start for want of a token, so a sign-in
    // starts it with its preview range. Null when a 401 stalled the loaded track:
    // player.js holds that position itself.
    private FileEntry? _startAfterSignIn;

    /// <summary>The playlist row that is loaded, for its cues and format.</summary>
    public FileEntry? CurrentFile { get; private set; }

    /// <summary>True when the source is uncompressed, i.e. a waveform can be sampled with range reads.</summary>
    public bool CurrentIsWav { get; private set; }

    /// <summary>Advance to the next row when a track ends. Remembered per browser.</summary>
    public bool ContinuousPlay { get; private set; }

    /// <summary>Gig preview: each track plays from its load cue to its last loop, then the next.</summary>
    public bool PreviewActive { get; private set; }

    /// <summary>The range the current track plays in gig preview; null outside it.</summary>
    public PreviewRange? CurrentRange { get; private set; }

    /// <summary>1-based position of the current track in the queue, 0 when not in it.</summary>
    public int QueuePosition => CurrentFile is null ? 0 : IndexOf(CurrentFile) + 1;
    public int QueueCount => _queue.Count;
    public bool HasQueue => _queue.Count > 0;

    /// <summary>Raised on every state transition so components can re-render.</summary>
    public event Action? Changed;

    public bool IsPlaying(string fileId) => CurrentFileId == fileId && State == PlaybackState.Playing;
    public bool IsLoading(string fileId) => CurrentFileId == fileId && State == PlaybackState.Loading;

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
    /// The playable rows, in the order the table shows them. Called again on every
    /// re-sort, so "next" always means the next row on screen. Rows with no Drive
    /// file are never queued, so advancing skips them.
    /// </summary>
    public void SetQueue(IEnumerable<FileEntry> rowsInTableOrder)
    {
        var current = CurrentFile is null ? -1 : IndexOf(CurrentFile);
        if (current >= 0) _lastIndex = current;

        _queue = rowsInTableOrder.Where(f => !string.IsNullOrEmpty(f.DriveFileId)).ToList();

        // A track cued by PlaybackMemory has no row until its playlist loads.
        if (CurrentFile is null && _localFileId is not null)
            CurrentFile = _queue.FirstOrDefault(f => f.DriveFileId == _localFileId);

        // A re-sort moves the current row; remember where it now sits.
        if (CurrentFile is not null && IndexOf(CurrentFile) is var moved and >= 0) _lastIndex = moved;
        Changed?.Invoke();
    }

    /// <summary>Leaving the playlist leaves nothing to advance through.</summary>
    public void ClearQueue()
    {
        _queue = [];
        _lastIndex = -1;
        if (PreviewActive) StopPreview();
        Changed?.Invoke();
    }

    public async Task LoadPreferencesAsync()
    {
        if (_preferencesLoaded) return;
        _preferencesLoaded = true;
        try { ContinuousPlay = await _js.InvokeAsync<string?>("localStorage.getItem", ContinuousPlayKey) == "1"; }
        catch { /* storage unavailable — default off */ }
        Changed?.Invoke();
    }

    public async Task SetContinuousPlayAsync(bool on)
    {
        ContinuousPlay = on;
        Changed?.Invoke();
        try { await _js.InvokeVoidAsync("localStorage.setItem", ContinuousPlayKey, on ? "1" : "0"); }
        catch { /* not worth failing over */ }
    }

    /// <summary>Starts gig preview from the first playable row in table order.</summary>
    public async Task StartPreviewAsync()
    {
        if (_queue.Count == 0) return;
        PreviewActive = true;
        _consecutiveFailures = 0;
        Changed?.Invoke();
        await PlayEntryAsync(_queue[0]);
    }

    /// <summary>Leaves preview mode; the current track keeps playing, unbounded.</summary>
    public void StopPreview()
    {
        PreviewActive = false;
        CurrentRange = null;
        if (_module is not null) _ = _module.InvokeVoidAsync("setBounds", null).AsTask();
        Changed?.Invoke();
    }

    public Task NextAsync() => StepAsync(+1);
    public Task PreviousAsync() => StepAsync(-1);

    private async Task StepAsync(int direction)
    {
        if (_queue.Count == 0) return;
        var index = CurrentFile is null ? -1 : IndexOf(CurrentFile);
        // The current row left the queue (or was never in it): continue from
        // where it last was rather than jumping back to the top.
        if (index < 0) index = direction > 0 ? _lastIndex : _lastIndex + 1;

        var target = index + direction;
        if (target < 0 || target >= _queue.Count)
        {
            // End of the list: stop, leaving the last track loaded and paused.
            if (direction > 0 && PreviewActive)
            {
                StopPreview();
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

    private int IndexOf(FileEntry file)
    {
        // By reference first: the same Drive file can sit in a playlist twice.
        var i = _queue.IndexOf(file);
        return i >= 0 ? i : _queue.FindIndex(f => f.DriveFileId == file.DriveFileId);
    }

    /* ---------- playback ---------- */

    public async Task ToggleAsync(FileEntry file, string token)
    {
        // `token` is whatever the page held when it rendered, which can be over an
        // hour old by now; every load below reads the current one instead.
        if (CurrentFileId != file.DriveFileId)
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
        await SyncProxyCookieAsync();
        await module.InvokeVoidAsync("play", file.DriveFileId, CurrentUrl, null, null);
    }

    /// <summary>
    /// Loads a row and plays it. In gig preview that means from its load cue, with
    /// the end bound armed; otherwise from the top.
    /// </summary>
    private async Task PlayEntryAsync(FileEntry file)
    {
        // Expired between tracks: one silent try before asking the user.
        var token = await CurrentTokenAsync() ?? await RenewTokenAsync();
        if (string.IsNullOrEmpty(token) && _cast.IsConnected)
        {
            _errors.Report("Cannot play the next track — the Google session expired",
                "Sign in again and press play.");
            if (PreviewActive) StopPreview();
            return;
        }

        var fileId = file.DriveFileId;
        var index = IndexOf(file);
        if (index >= 0) _lastIndex = index;

        CurrentFile = file;
        CurrentTitle = file.Track?.Title ?? Path.GetFileName(file.Track?.Path ?? "");
        CurrentUrl = DriveAudio.UrlFor(fileId);
        CurrentIsWav = DriveAudio.IsUncompressed(file);
        _localTime = 0;
        // Traktor's PLAYTIME until the player reports its own; with sliced
        // range responses that can take a while.
        _localDuration = file.Track?.PlaytimeFloat ?? 0;
        CurrentRange = PreviewActive ? GigPreview.RangeFor(file.Track, _localDuration) : null;

        var start = CurrentRange?.Start ?? 0;

        if (_cast.IsConnected)
        {
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
        _localFileId = fileId;
        _localState = PlaybackState.Loading;
        Changed?.Invoke();
        await SyncProxyCookieAsync();
        await module.InvokeVoidAsync("play", fileId, CurrentUrl, start > 0 ? start : null, CurrentRange?.End);
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
        // from here, and the waveform gets its cue markers. Otherwise SetQueue
        // adopts it when the playlist's rows arrive.
        CurrentFile = _queue.FirstOrDefault(f => f.DriveFileId == fileId);
        if (CurrentFile is not null) _lastIndex = IndexOf(CurrentFile);
        CurrentTitle = title;
        CurrentUrl = DriveAudio.UrlFor(fileId);
        CurrentIsWav = isWav;
        _localFileId = fileId;
        _localTime = position;
        _localDuration = duration;
        _localState = PlaybackState.Paused;

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
        if (_cast.IsConnected) { await _cast.PlayOrPauseAsync(); return; }
        if (NeedsSignIn) { await SignInToContinueAsync(); return; }
        if (_module is null || _localFileId is null || CurrentUrl is null) return;
        await SyncProxyCookieAsync();
        await _module.InvokeVoidAsync("play", _localFileId, CurrentUrl, null, null);
    }

    public async Task PauseAsync()
    {
        if (_cast.IsConnected) { await _cast.PlayOrPauseAsync(); return; }
        if (_module is null) return;
        await _module.InvokeVoidAsync("pause");
    }

    public async Task SeekAsync(double seconds)
    {
        if (_cast.IsConnected) { await _cast.SeekAsync(seconds); return; }
        if (_module is null) return;
        await _module.InvokeVoidAsync("seek", seconds);
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
            if (PreviewActive) _ = AdvanceAsync();
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
        else if (state == "idle") { _localFileId = null; _localTime = 0; }
        else if (fileId is not null) _localFileId = fileId;

        if (state == "playing") _consecutiveFailures = 0;

        switch (state)
        {
            case "unauthorized":
                // player.js already tried one silent renewal and holds the position.
                // Not an error to skip past, and not a reason to leave preview mode:
                // the player bar's sign-in button continues from right here.
                _startAfterSignIn = null;
                break;
            case "error" when PreviewActive:
                if (++_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    StopPreview();
                    _errors.Report("Gig preview stopped",
                        $"{MaxConsecutiveFailures} tracks in a row failed to play. Last: {CurrentTitle} (fileId {fileId}).");
                }
                else
                {
                    _errors.Info($"Skipped \"{CurrentTitle}\" — it would not play", $"fileId {fileId}.");
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
            case "ended" when PreviewActive || ContinuousPlay:
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
