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
    double Duration,
    string? IdleReason = null);

/// <summary>What cast.js reports after asking the server for a media ticket.</summary>
public record CastTicketResponse(int Status, string? Ticket, string? RenewKey, double ExpiresAt);

/// <summary>
/// Playback on a Cast device. Deliberately shaped like <see cref="PlayerService"/>
/// so components can ask the same questions of either — the difference that matters
/// is that the Cast device, not this browser, fetches the audio.
/// </summary>
public class CastService : IAsyncDisposable
{
    /// <summary>A token (or ticket) this close to expiry is replaced before the device notices.</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often the current token is pushed to the server even when nothing
    /// changed. The server holds tokens in memory only; after a restart this puts
    /// the device's token back before its buffer runs out.
    /// </summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(60);

    /// <summary>After a renewal the server could not take, wait this long before the next try.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    private readonly IJSRuntime _js;
    private readonly NavigationManager _nav;
    private readonly AppErrors _errors;

    private IJSObjectReference? _module;
    private DotNetObjectReference<CastService>? _self;
    private Task<IJSObjectReference>? _init;

    // What is on the device right now, so a ticket that runs out can re-issue the
    // same request with a fresh URL.
    private (string FileId, string ContentType, string Title, string Artist)? _current;
    private bool _refreshing;
    private bool _refreshDeclined;
    // The ticket in the URL the device holds. The Google token stays on the
    // server behind it, so a renewal is a call to the server, not a new LOAD.
    private (string Value, string RenewKey, DateTimeOffset ExpiresAt)? _ticket;
    // The token the server holds for that ticket. auth.js renews the browser's
    // token on its own schedule; a newer stored token means "push it".
    private string? _sentToken;
    private DateTimeOffset _lastPushed;
    private DateTimeOffset _nextAttempt;

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

    /// <summary>The device played a track to its end (not stopped, not failed).</summary>
    public event Action? Finished;

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
        await RevokeTicketAsync();
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

    /// <summary>
    /// Always loads, even the file already on the device, starting at
    /// <paramref name="startTime"/>: gig preview and auto-advance both need a
    /// fresh LOAD, never the toggle <see cref="ToggleAsync"/> would do.
    /// </summary>
    public async Task LoadAsync(FileEntry file, string token, double startTime)
    {
        await ModuleAsync();
        CurrentFileId = file.DriveFileId;
        State = PlaybackState.Loading;
        _refreshDeclined = false;
        Changed?.Invoke();

        _current = (file.DriveFileId, ContentTypeOf(file),
                    file.Track?.Title ?? file.DriveFileName, file.Track?.Artist ?? "");
        await SendLoadAsync(token, startTime);
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
        await RevokeTicketAsync();
        await _module.InvokeVoidAsync("stop");
    }

    public async Task SeekAsync(double seconds)
    {
        if (_module is null) return;
        await _module.InvokeVoidAsync("seek", seconds);
    }

    /// <summary>
    /// Trades the token for a ticket scoped to this file, then hands the device a
    /// URL with only the ticket in it. The one previous ticket is revoked.
    /// </summary>
    private async Task SendLoadAsync(string token, double startTime)
    {
        if (_current is not { } c || _module is null) return;

        await RevokeTicketAsync();
        var minted = await _module.InvokeAsync<CastTicketResponse>("mintTicket",
            c.FileId, token, await TokenExpiresAtAsync());
        if (minted.Ticket is not { Length: > 0 } ticket || minted.RenewKey is not { Length: > 0 } renewKey)
        {
            OnCastFailed(minted.Status == 0
                ? "The server could not be reached for a Cast ticket"
                : $"The server refused a Cast ticket (HTTP {minted.Status})");
            return;
        }

        _ticket = (ticket, renewKey, DateTimeOffset.FromUnixTimeMilliseconds((long)minted.ExpiresAt));
        _sentToken = token;
        _lastPushed = DateTimeOffset.UtcNow;
        await _module.InvokeAsync<bool>("load",
            c.FileId,
            DriveAudio.CastUrlFor(_nav.BaseUri, c.FileId, ticket),
            c.ContentType,
            c.Title,
            c.Artist,
            startTime);
    }

    private async Task RevokeTicketAsync()
    {
        if (_ticket is not { } t || _module is null) return;
        _ticket = null;
        try { await _module.InvokeVoidAsync("revokeTicket", t.Value, t.RenewKey); }
        catch (JSException) { /* best effort: it dies with its token anyway */ }
    }

    private async Task<double?> TokenExpiresAtAsync()
    {
        try { return await _js.InvokeAsync<double?>("authTokenExpiresAt"); }
        catch (JSException) { return null; }
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

        // FINISHED is the receiver's own word for it. A receiver that does not
        // report a reason still counts if it went idle at the very end.
        var finished = previousState == PlaybackState.Playing && State == PlaybackState.Idle
            && (snapshot.IdleReason == "FINISHED"
                || (snapshot.IdleReason is null && snapshot.Duration > 0
                    && snapshot.CurrentTime >= snapshot.Duration - 1.5));

        // Idle means nothing is on the device, so the casting bar must stop showing
        // the track that just finished, and its ticket has nothing left to fetch.
        if (State == PlaybackState.Idle)
        {
            _current = null;
            _ = RevokeTicketAsync();
        }

        if (IsConnected && State == PlaybackState.Playing) _ = KeepTicketAliveAsync();

        // Everything except the position is a state change; the position alone is a
        // tick, and only the casting bar cares about those.
        if (wasAvailable != IsAvailable || wasConnected != IsConnected
            || previousState != State || previousFile != CurrentFileId)
            Changed?.Invoke();
        else
            Progress?.Invoke();

        if (finished) Finished?.Invoke();
    }

    [JSInvokable]
    public void OnCastFailed(string detail)
    {
        State = PlaybackState.Error;
        _errors.Report("Casting failed", detail);
        Changed?.Invoke();
    }

    /// <summary>
    /// The device's URL carries a ticket; the Google token behind it is on the
    /// server and lives ~1h, while a recorded set can run for hours. Keep the
    /// server's copy current so the device never notices:
    /// <list type="bullet">
    /// <item>a token renewed elsewhere (auth.js's own schedule, a click) is pushed;</item>
    /// <item>a token about to expire is renewed here, then pushed;</item>
    /// <item>otherwise the current token is pushed once a <see cref="Heartbeat"/>,
    ///   which restores the grant if the server restarted.</item>
    /// </list>
    /// None of these touch the device. Only a ticket at its hard cap, or one the
    /// server no longer accepts, needs a new URL: then the load is re-issued at the
    /// current position, which costs one rebuffer.
    /// </summary>
    private async Task KeepTicketAliveAsync()
    {
        if (_refreshing || _current is null || _ticket is null || _module is null) return;
        var now = DateTimeOffset.UtcNow;
        if (now < _nextAttempt) return;

        _refreshing = true;
        try
        {
            string? stored;
            try { stored = await _js.InvokeAsync<string?>("authGetToken"); }
            catch (JSException) { stored = null; }

            if (_ticket is { } t && t.ExpiresAt - now < RefreshLead)
            {
                await ReloadWithNewTicketAsync(stored ?? _sentToken);
                return;
            }

            string? push = null;
            if (!string.IsNullOrEmpty(stored) && stored != _sentToken)
            {
                // Renewed already: hand the server the new token rather than
                // asking Google for a third one.
                push = stored;
            }
            else if (await ExpiresWithinLeadAsync() && !_refreshDeclined)
            {
                var renewed = await _js.InvokeAsync<string?>("authRefreshToken");
                if (string.IsNullOrEmpty(renewed))
                {
                    // Declined once means declined for this session; retrying every
                    // second would bury the page in identical errors.
                    _refreshDeclined = true;
                    _errors.Report("Casting will stop when the Google token expires",
                        "Silent token refresh was refused. Sign in again to keep playing on the TV.");
                }
                else
                {
                    push = renewed;
                }
            }

            if (push is null && now - _lastPushed >= Heartbeat) push = stored ?? _sentToken;
            if (push is null || _ticket is not { } current) return;

            var status = await _module.InvokeAsync<int>("renewTicket",
                current.Value, current.RenewKey, push, await TokenExpiresAtAsync());
            switch (status)
            {
                case StatusCodes.NoContent:
                    _sentToken = push;
                    _lastPushed = DateTimeOffset.UtcNow;
                    break;
                case StatusCodes.Gone or StatusCodes.Forbidden:
                    // Past its cap, or signed with a key the server no longer has:
                    // the device's URL is dead, so give it a new one.
                    await ReloadWithNewTicketAsync(push);
                    break;
                default:
                    // Unreachable or failing: the device still has its buffer, and
                    // the token on the server may still be good. Try again shortly.
                    _nextAttempt = DateTimeOffset.UtcNow.Add(RetryAfter);
                    break;
            }
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

    private async Task<bool> ExpiresWithinLeadAsync()
    {
        var expiresAt = await TokenExpiresAtAsync();
        if (expiresAt is null or 0) return false;
        var remaining = DateTimeOffset.FromUnixTimeMilliseconds((long)expiresAt.Value) - DateTimeOffset.UtcNow;
        return remaining <= RefreshLead;
    }

    private async Task ReloadWithNewTicketAsync(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        await SendLoadAsync(token, CurrentTime);
    }

    /// <summary>The few HTTP statuses the ticket endpoints answer with.</summary>
    private static class StatusCodes
    {
        public const int NoContent = 204;
        public const int Forbidden = 403;
        public const int Gone = 410;
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
