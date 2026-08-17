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
/// </summary>
public class PlayerService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly AppErrors _errors;
    private readonly CastService _cast;
    private IJSObjectReference? _module;
    private DotNetObjectReference<PlayerService>? _self;

    private string? _localFileId;
    private PlaybackState _localState = PlaybackState.Idle;
    private double _localTime;
    private double _localDuration;

    public PlayerService(IJSRuntime js, AppErrors errors, CastService cast)
    {
        _js = js;
        _errors = errors;
        _cast = cast;
        _cast.Changed += OnCastChanged;
        _cast.Progress += OnCastProgress;
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

    private void OnCastProgress() => Changed?.Invoke();

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
    public string? CurrentUrl { get; private set; }

    /// <summary>True when the source is uncompressed, i.e. a waveform can be sampled cheaply.</summary>
    public bool CurrentIsWav { get; private set; }

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

    public async Task ToggleAsync(FileEntry file, string token)
    {
        var fileId = file.DriveFileId;

        if (CurrentFileId != fileId)
        {
            CurrentTitle = file.Track?.Title ?? Path.GetFileName(file.Track?.Path ?? "");
            CurrentUrl = DriveAudio.UrlFor(fileId, token);
            CurrentIsWav = DriveAudio.IsUncompressed(file);
            _localTime = 0;
            // Traktor's PLAYTIME until the player reports its own; with sliced
            // range responses that can take a while.
            _localDuration = file.Track?.PlaytimeFloat ?? 0;
        }

        if (_cast.IsConnected)
        {
            await _cast.ToggleAsync(file, token);
            return;
        }

        var module = await ModuleAsync();

        if (_localFileId == fileId && _localState is PlaybackState.Playing or PlaybackState.Loading)
        {
            await module.InvokeVoidAsync("pause");
            return;
        }

        _localFileId = fileId;
        _localState = PlaybackState.Loading;
        Changed?.Invoke();
        await module.InvokeVoidAsync("play", fileId, CurrentUrl);
    }

    /// <summary>Play or pause whatever is loaded, on whichever output owns it.</summary>
    public async Task ResumeAsync()
    {
        if (_cast.IsConnected) { await _cast.PlayOrPauseAsync(); return; }
        if (_module is null || _localFileId is null || CurrentUrl is null) return;
        await _module.InvokeVoidAsync("play", _localFileId, CurrentUrl);
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
        _localState = state switch
        {
            "loading" or "ready" => _localState == PlaybackState.Playing ? PlaybackState.Playing : PlaybackState.Loading,
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

        switch (state)
        {
            case "unauthorized":
                _errors.Report("Playback failed — Drive rejected the token for this file",
                    $"fileId {fileId}. The audio proxy returned 401/403, so the token likely expired mid-session.");
                break;
            case "error":
                _errors.Report("Playback failed",
                    $"fileId {fileId}. The browser could not decode or fetch the audio.");
                break;
            case "blocked":
                _errors.Report("Playback blocked by the browser",
                    "Autoplay was refused. Click play again — browsers require a direct user gesture.");
                break;
        }

        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cast.Changed -= OnCastChanged;
        _cast.Progress -= OnCastProgress;

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
