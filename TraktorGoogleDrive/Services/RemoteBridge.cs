using Microsoft.JSInterop;

using TraktorGoogleDrive.Models;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Connects <see cref="PlayerService"/> to the room (ADR 20261018-63): publishes
/// this tab's playback when it is the player, carries out commands from
/// controllers, pauses when another tab starts playing, and decides when this
/// tab should hold a connection at all (§2.1), because an open socket keeps a
/// Cloud Run instance billed.
/// </summary>
public sealed class RemoteBridge : IAsyncDisposable
{
    /// <summary>Republished this often while playing, as drift correction for controllers.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PolicyInterval = TimeSpan.FromSeconds(5);

    private readonly PlayerService _player;
    private readonly RemoteService _remote;
    private readonly IJSRuntime _js;

    private DotNetObjectReference<RemoteBridge>? _self;
    private CancellationTokenSource? _loop;
    private bool _started;
    private bool _evaluating;

    private bool _visible = true;
    private DateTime _hiddenSince = DateTime.UtcNow;
    private DateTime _lastPlayingAt = DateTime.UtcNow;
    private TimeSpan _playerIdle = TimeSpan.FromMinutes(15);
    private TimeSpan _controllerIdle = TimeSpan.FromMinutes(1);

    private long _seq;
    private bool _published;
    private string? _lastKey;
    private DateTime _lastPublishAt;
    private double _lastPosition;
    private bool _lastPlaying;

    public RemoteBridge(PlayerService player, RemoteService remote, IJSRuntime js)
    {
        _player = player;
        _remote = remote;
        _js = js;
    }

    /// <summary>Safe to call on every render; only the first call does anything.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        await _remote.InitializeAsync();
        _self = DotNetObjectReference.Create(this);
        _visible = await _remote.VisibleAsync(_self);
        var (playerMs, controllerMs) = await _remote.IdleTimeoutsAsync();
        _playerIdle = TimeSpan.FromMilliseconds(playerMs);
        _controllerIdle = TimeSpan.FromMilliseconds(controllerMs);

        _player.Changed += OnPlayerChanged;
        _remote.Joined += OnJoined;
        _remote.Superseded += OnSuperseded;
        _remote.CommandHandler = HandleAsync;

        _loop = new CancellationTokenSource();
        _ = PolicyLoopAsync(_loop.Token);
        await EvaluateAsync();
    }

    /// <summary>Called by the toggle, so switching on connects without waiting for a tick.</summary>
    public Task EvaluateNowAsync() => EvaluateAsync();

    [JSInvokable]
    public Task OnVisibilityChanged(bool visible)
    {
        _visible = visible;
        if (!visible) _hiddenSince = DateTime.UtcNow;
        return EvaluateAsync();
    }

    private async Task PolicyLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PolicyInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await EvaluateAsync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Something is loaded in this tab (here or on its TV), so it is a player, not a controller.</summary>
    private bool LoadedHere => !_player.IsRemote && _player.CurrentFileId is not null;

    private bool PlayingHere => LoadedHere && _player.State is PlaybackState.Playing or PlaybackState.Loading;

    /// <summary>
    /// ADR 20261018-63 §2.1. A player stays connected while it plays, while it is
    /// visible, and for 15 minutes after it last played; a controller while it is
    /// visible and for a minute after.
    /// </summary>
    private bool WantConnection()
    {
        var now = DateTime.UtcNow;
        if (LoadedHere) return PlayingHere || _visible || now - _lastPlayingAt < _playerIdle;
        return _visible || now - _hiddenSince < _controllerIdle;
    }

    private async Task EvaluateAsync()
    {
        if (_evaluating) return;
        _evaluating = true;
        try
        {
            var want = _remote.Enabled && WantConnection();

            // A token is needed to get a ticket, i.e. to connect, but not to stay
            // connected: a player whose token ran out stays visible, so the
            // tablet can say it needs a sign-in rather than lose it (§5.4).
            if (want && !_remote.IsConnected && !_remote.IsReconnecting && await HasTokenAsync())
            {
                await _remote.ConnectAsync();
            }
            else if (!want && (_remote.IsConnected || _remote.IsReconnecting))
            {
                await _remote.DisconnectAsync();
                _published = false;
                _lastKey = null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Remote] {ex.Message}");
        }
        finally
        {
            _evaluating = false;
        }
    }

    private async Task<bool> HasTokenAsync()
    {
        try { return !string.IsNullOrEmpty(await _js.InvokeAsync<string?>("authGetToken")); }
        catch (JSException) { return false; }
    }

    private void OnPlayerChanged()
    {
        if (PlayingHere) _lastPlayingAt = DateTime.UtcNow;
        if (_remote.IsConnected && !_player.IsRemote) _ = PublishAsync(force: false);
    }

    // A new connection (first join, or after Cloud Run's hourly cut) starts with
    // an empty room on the server's side: tell it everything again.
    private void OnJoined()
    {
        _published = false;
        _lastKey = null;
        _ = PublishAsync(force: true);
    }

    // Last press wins (§4.4): another tab started playing.
    private void OnSuperseded()
    {
        if (PlayingHere) _ = _player.PauseAsync();
    }

    private async Task PublishAsync(bool force)
    {
        if (_player.IsRemote) return;
        var snapshot = Build();
        if (snapshot.Track is null && !_published) return; // nothing loaded, nothing said yet

        var now = DateTime.UtcNow;
        var playing = snapshot.State == nameof(PlaybackState.Playing);
        var key = string.Join('|', snapshot.Track?.FileId, snapshot.State, snapshot.Continuous, snapshot.PreviewActive,
            snapshot.Preview, snapshot.QueuePosition, snapshot.QueueCount, snapshot.CastDeviceName,
            snapshot.NeedsSignIn, snapshot.Track?.Title, Math.Round(snapshot.Track?.Duration ?? 0));
        var expected = _lastPosition + (_lastPlaying ? (now - _lastPublishAt).TotalSeconds : 0);

        var due = force
            || key != _lastKey
            || Math.Abs(snapshot.Position - expected) > 2 // a seek, or drift
            || (playing && now - _lastPublishAt >= Heartbeat);
        if (!due) return;

        _lastKey = key;
        _lastPublishAt = now;
        _lastPosition = snapshot.Position;
        _lastPlaying = playing;
        _published = snapshot.Track is not null;
        await _remote.PublishAsync(snapshot with { Seq = ++_seq });
    }

    private PlayerSnapshot Build()
    {
        var fileId = _player.CurrentFileId;
        var state = _player.AutoplayBlocked && _player.State == PlaybackState.Error
            ? "Blocked"
            : _player.State.ToString();
        var track = fileId is null
            ? null
            : new RemoteTrack(fileId, _player.CurrentTitle,
                _player.CurrentFile is not null ? _player.QueuePlaylistUuid : null,
                _player.CurrentIsWav, _player.Duration);
        var range = _player.PreviewActive ? _player.CurrentRange : null;

        return new PlayerSnapshot(
            PlayerSnapshot.Version,
            Seq: 0,
            // Overwritten by the hub with what this connection joined as.
            DeviceId: _remote.Me?.DeviceId ?? "",
            DeviceName: _remote.Me?.Name ?? "",
            track,
            state,
            _player.CurrentTime,
            _player.ContinuousPlay,
            _player.PreviewActive,
            range is null ? null : new RemotePreview(range.Start, range.End, range.Note),
            _player.QueuePosition,
            _player.QueueCount,
            _player.IsCasting ? _player.CastDeviceName ?? "TV" : null,
            // PlayerService's own verdict (the token-refresh change): playback
            // stopped and only a click on that device can sign it in again.
            NeedsSignIn: _player.NeedsSignIn);
    }

    /// <summary>Carries out a controller's command on this tab's playback.</summary>
    private async Task<CommandResult> HandleAsync(RemoteCommand command)
    {
        if (!LoadedHere) return CommandResult.Reject(command.CommandId, "not-a-player");

        try
        {
            switch (command.Kind)
            {
                case RemoteCommandKind.Play:
                    // Pause and play are absolute: on Cast both are one toggle, so
                    // guard on the state rather than flip it twice.
                    if (!PlayingHere) await _player.ResumeAsync();
                    break;
                case RemoteCommandKind.Pause:
                    if (PlayingHere) await _player.PauseAsync();
                    break;
                case RemoteCommandKind.Seek when command.Seconds is { } seconds:
                    await _player.SeekAsync(seconds);
                    break;
                case RemoteCommandKind.Next:
                case RemoteCommandKind.Previous:
                    if (!_player.HasQueue) return CommandResult.Reject(command.CommandId, "no-queue");
                    await (command.Kind == RemoteCommandKind.Next ? _player.NextAsync() : _player.PreviousAsync());
                    break;
                case RemoteCommandKind.RequestSnapshot:
                    break;
                default:
                    return CommandResult.Reject(command.CommandId, "unknown-command");
            }
        }
        catch (Exception ex)
        {
            return CommandResult.Reject(command.CommandId, ex.Message);
        }

        _ = PublishAsync(force: true);
        return CommandResult.Accept(command.CommandId);
    }

    public ValueTask DisposeAsync()
    {
        _loop?.Cancel();
        _player.Changed -= OnPlayerChanged;
        _remote.Joined -= OnJoined;
        _remote.Superseded -= OnSuperseded;
        _self?.Dispose();
        return ValueTask.CompletedTask;
    }
}
