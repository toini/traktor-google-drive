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
    private IJSObjectReference? _module;
    private DotNetObjectReference<PlayerService>? _self;

    public PlayerService(IJSRuntime js) => _js = js;

    public string? CurrentFileId { get; private set; }
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

    public async Task ToggleAsync(string fileId, string url)
    {
        var module = await ModuleAsync();

        if (CurrentFileId == fileId && State is PlaybackState.Playing or PlaybackState.Loading)
        {
            await module.InvokeVoidAsync("pause");
            return;
        }

        CurrentFileId = fileId;
        State = PlaybackState.Loading;
        Changed?.Invoke();
        await module.InvokeVoidAsync("play", fileId, url);
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
            "paused" => PlaybackState.Paused,
            "unauthorized" => PlaybackState.Unauthorized,
            "error" or "blocked" => PlaybackState.Error,
            _ => PlaybackState.Idle,
        };

        if (state is "ended" or "idle") CurrentFileId = null;
        else if (fileId is not null) CurrentFileId = fileId;

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
