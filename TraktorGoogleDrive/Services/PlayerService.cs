using Microsoft.JSInterop;

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
/// </summary>
public class PlayerService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly AppErrors _errors;
    private IJSObjectReference? _module;
    private DotNetObjectReference<PlayerService>? _self;

    public PlayerService(IJSRuntime js, AppErrors errors)
    {
        _js = js;
        _errors = errors;
    }

    public string? CurrentFileId { get; private set; }
    public string? CurrentTitle { get; private set; }
    public string? CurrentUrl { get; private set; }
    /// <summary>True when the source is uncompressed, i.e. a waveform can be sampled cheaply.</summary>
    public bool CurrentIsWav { get; private set; }
    public double CurrentTime { get; private set; }
    public double Duration { get; private set; }
    public PlaybackState State { get; private set; } = PlaybackState.Idle;

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

    public async Task ToggleAsync(string fileId, string url, string? title = null, bool isWav = false, double? durationHint = null)
    {
        var module = await ModuleAsync();

        if (CurrentFileId == fileId && State is PlaybackState.Playing or PlaybackState.Loading)
        {
            await module.InvokeVoidAsync("pause");
            return;
        }

        if (CurrentFileId != fileId)
        {
            CurrentTime = 0;
            // Traktor's PLAYTIME until the media element reports its own; with
            // sliced range responses that can take a while.
            Duration = durationHint ?? 0;
        }

        CurrentFileId = fileId;
        CurrentTitle = title;
        CurrentUrl = url;
        CurrentIsWav = isWav;
        State = PlaybackState.Loading;
        Changed?.Invoke();
        await module.InvokeVoidAsync("play", fileId, url);
    }

    public async Task ResumeAsync()
    {
        if (_module is null || CurrentFileId is null || CurrentUrl is null) return;
        await _module.InvokeVoidAsync("play", CurrentFileId, CurrentUrl);
    }

    public async Task PauseAsync()
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("pause");
    }

    public async Task SeekAsync(double seconds)
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("seek", seconds);
    }

    [JSInvokable]
    public void OnProgress(double currentTime, double duration)
    {
        CurrentTime = currentTime;
        // A sliced 206 response can leave duration Infinity until enough is
        // buffered; the track's own PLAYTIME is a better source in that case.
        Duration = double.IsFinite(duration) && duration > 0 ? duration : Duration;
        Changed?.Invoke();
    }

    public async Task StopAsync()
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("stop");
    }

    [JSInvokable]
    public void OnPlaybackStateChanged(string state, string? fileId)
    {
        State = state switch
        {
            "loading" or "ready" => State == PlaybackState.Playing ? PlaybackState.Playing : PlaybackState.Loading,
            "playing" => PlaybackState.Playing,
            // Keep the track loaded and paused rather than clearing it: a set
            // that just finished is exactly the one you want to scrub back into.
            "ended" => PlaybackState.Paused,
            "paused" => PlaybackState.Paused,
            "unauthorized" => PlaybackState.Unauthorized,
            "error" or "blocked" => PlaybackState.Error,
            _ => PlaybackState.Idle,
        };

        if (state == "ended") CurrentTime = 0;
        else if (state == "idle") { CurrentFileId = null; CurrentTime = 0; }
        else if (fileId is not null) CurrentFileId = fileId;

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
