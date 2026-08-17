using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using TraktorGoogleDrive.Models;

namespace TraktorGoogleDrive.Services;

/// <summary>What cast.js reports after every visible change on the Cast device.</summary>
public record CastSnapshot(
    bool Available,
    bool Connected,
    string? DeviceName,
    string? State,
    string? FileId,
    double CurrentTime,
    double Duration);

/// <summary>
/// Playback on a Cast device. Deliberately shaped like <see cref="PlayerService"/>
/// so components can ask the same questions of either — the difference that matters
/// is that the Cast device, not this browser, fetches the audio.
/// </summary>
public class CastService : IAsyncDisposable
{
    /// <summary>A token this close to expiry is replaced before the device notices.</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(5);

    private readonly IJSRuntime _js;
    private readonly NavigationManager _nav;
    private readonly AppErrors _errors;

    private IJSObjectReference? _module;
    private DotNetObjectReference<CastService>? _self;
    private Task<IJSObjectReference>? _init;

    // What is on the device right now, so a token refresh can re-issue the same
    // request with a fresh URL.
    private (string FileId, string ContentType, string Title, string Artist)? _current;
    private bool _refreshing;
    private bool _refreshDeclined;

    public CastService(IJSRuntime js, NavigationManager nav, AppErrors errors)
    {
        _js = js;
        _nav = nav;
        _errors = errors;
    }

    public bool IsAvailable { get; private set; }
    public bool IsConnected { get; private set; }
    public string? DeviceName { get; private set; }
    public string? CurrentFileId { get; private set; }
    public string? CurrentTitle => _current?.Title;
    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public double CurrentTime { get; private set; }
    public double Duration { get; private set; }

    /// <summary>Session and playback transitions.</summary>
    public event Action? Changed;

    /// <summary>Position ticks, roughly once a second. Separate from
    /// <see cref="Changed"/> so a long playlist is not re-rendered every second.</summary>
    public event Action? Progress;

    public bool IsPlaying(string fileId) => CurrentFileId == fileId && State == PlaybackState.Playing;
    public bool IsLoading(string fileId) => CurrentFileId == fileId && State == PlaybackState.Loading;

    public async Task EnsureInitializedAsync()
    {
        try { await ModuleAsync(); }
        catch (JSException ex) { _errors.Report("Could not load the Cast sender", ex); }
    }

    private Task<IJSObjectReference> ModuleAsync() => _init ??= ImportAsync();

    private async Task<IJSObjectReference> ImportAsync()
    {
        _module = await _js.InvokeAsync<IJSObjectReference>("import", "./cast.js");
        _self = DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("init", _self);
        return _module;
    }

    public async Task ConnectAsync()
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("connect");
    }

    public async Task DisconnectAsync()
    {
        if (_module is null) return;
        _current = null;
        await _module.InvokeVoidAsync("disconnect");
    }

    /// <summary>
    /// Same contract as <see cref="PlayerService.ToggleAsync"/>: pressing the track
    /// already on the device toggles it rather than reloading from the start.
    /// </summary>
    public async Task ToggleAsync(FileEntry file, string token)
    {
        var module = await ModuleAsync();

        if (CurrentFileId == file.DriveFileId && State is not PlaybackState.Idle and not PlaybackState.Error)
        {
            await module.InvokeVoidAsync("playOrPause");
            return;
        }

        CurrentFileId = file.DriveFileId;
        State = PlaybackState.Loading;
        _refreshDeclined = false;
        Changed?.Invoke();

        _current = (file.DriveFileId, ContentTypeOf(file),
                    file.Track?.Title ?? file.DriveFileName, file.Track?.Artist ?? "");
        await SendLoadAsync(token, 0);
    }

    public async Task PlayOrPauseAsync()
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("playOrPause");
    }

    public async Task StopAsync()
    {
        if (_module is null) return;
        _current = null;
        await _module.InvokeVoidAsync("stop");
    }

    public async Task SeekAsync(double seconds)
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("seek", seconds);
    }

    private async Task SendLoadAsync(string token, double startTime)
    {
        if (_current is not { } c || _module is null) return;

        await _module.InvokeAsync<bool>("load",
            c.FileId,
            DriveAudio.AbsoluteUrlFor(_nav.BaseUri, c.FileId, token),
            c.ContentType,
            c.Title,
            c.Artist,
            startTime);
    }

    /// Cast needs a content type in MediaInfo and Drive already reports the real
    /// one; the fallback matters only for a track Drive did not type.
    private static string ContentTypeOf(FileEntry file) =>
        string.IsNullOrWhiteSpace(file.DriveFileMimeType) ? "audio/wav" : file.DriveFileMimeType;

    [JSInvokable]
    public void OnCastChanged(CastSnapshot snapshot)
    {
        var wasAvailable = IsAvailable;
        var wasConnected = IsConnected;
        var previousState = State;
        var previousFile = CurrentFileId;

        IsAvailable = snapshot.Available;
        IsConnected = snapshot.Connected;
        DeviceName = snapshot.DeviceName;
        CurrentTime = snapshot.CurrentTime;
        Duration = snapshot.Duration;

        State = snapshot.State switch
        {
            "PLAYING" => PlaybackState.Playing,
            "PAUSED" => PlaybackState.Paused,
            "BUFFERING" => PlaybackState.Loading,
            // IDLE arrives both before the first byte and after the file ends, so
            // it must not demote a load that has not started playing yet.
            _ => previousState == PlaybackState.Loading ? PlaybackState.Loading : PlaybackState.Idle,
        };

        CurrentFileId = State == PlaybackState.Idle ? null : snapshot.FileId ?? previousFile;

        // Idle means nothing is on the device, so the casting bar must stop showing
        // the track that just finished.
        if (State == PlaybackState.Idle) _current = null;

        if (IsConnected && State == PlaybackState.Playing) _ = RefreshTokenIfExpiringAsync();

        // Everything except the position is a state change; the position alone is a
        // tick, and only the casting bar cares about those.
        if (wasAvailable != IsAvailable || wasConnected != IsConnected
            || previousState != State || previousFile != CurrentFileId)
            Changed?.Invoke();
        else
            Progress?.Invoke();
    }

    [JSInvokable]
    public void OnCastFailed(string detail)
    {
        State = PlaybackState.Error;
        _errors.Report("Casting failed", detail);
        Changed?.Invoke();
    }

    /// <summary>
    /// The device holds the access token inside its media URL, and a recorded set
    /// outlives the ~1h token. Re-issue the load with a fresh token at the current
    /// position rather than letting the device hit a 401 mid-set.
    /// </summary>
    private async Task RefreshTokenIfExpiringAsync()
    {
        // Declined once means declined for this session; retrying every second
        // would bury the page in identical errors.
        if (_refreshing || _refreshDeclined || _current is null) return;

        double? expiresAt;
        try { expiresAt = await _js.InvokeAsync<double?>("authTokenExpiresAt"); }
        catch (JSException) { return; }

        if (expiresAt is null or 0) return;

        var remaining = DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAt.Value) - DateTimeOffset.UtcNow;
        if (remaining > RefreshLead) return;

        _refreshing = true;
        var resumeAt = CurrentTime;
        try
        {
            var token = await _js.InvokeAsync<string?>("authRefreshToken");
            if (string.IsNullOrEmpty(token))
            {
                _refreshDeclined = true;
                _errors.Report("Casting will stop when the Google token expires",
                    "Silent token refresh was refused. Sign in again to keep playing on the TV.");
                return;
            }
            await SendLoadAsync(token, resumeAt);
        }
        catch (JSException ex)
        {
            _errors.Report("Could not refresh the Google token for the Cast device", ex);
        }
        finally
        {
            _refreshing = false;
        }
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
