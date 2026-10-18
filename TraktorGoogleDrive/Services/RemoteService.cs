using System.Net.Http.Headers;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

using TraktorGoogleDrive.Models;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// The connection to this Google account's room on the remote-control hub
/// (ADR 20261018-63), and what it says about the other devices. It knows nothing
/// about playback: <see cref="RemoteBridge"/> connects it to
/// <see cref="PlayerService"/>, and PlayerService reads <see cref="RemoteSnapshot"/>
/// to route to another device the way it already routes to a TV.
/// </summary>
public sealed class RemoteService : IAsyncDisposable
{
    private const string HubPath = "api/remote/hub";
    private const string TicketPath = "api/remote/ticket";

    private readonly IJSRuntime _js;
    private readonly NavigationManager _nav;
    private IJSObjectReference? _module;
    private HubConnection? _hub;
    private CancellationTokenSource? _ticker;
    private DateTime _snapshotReceivedAt;
    private TaskCompletionSource _firstRoom = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RemoteService(IJSRuntime js, NavigationManager nav)
    {
        _js = js;
        _nav = nav;
    }

    public bool Initialized { get; private set; }

    /// <summary>Switched on per browser while the feature is new (remote.js).</summary>
    public bool Enabled { get; private set; }

    public DeviceHello? Me { get; private set; }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>Connected before, now trying again. The bar says "Looking for…".</summary>
    public bool IsReconnecting => _hub?.State == HubConnectionState.Reconnecting;

    public RoomState? Room { get; private set; }

    /// <summary>
    /// The active player's snapshot when the active player is another tab. Null
    /// when nobody is playing, or when this tab is the player.
    /// </summary>
    public PlayerSnapshot? RemoteSnapshot =>
        IsConnected && Room is { ActiveConnectionId: { } active, Snapshot: { Track: not null } snapshot }
        && active != _hub?.ConnectionId
            ? snapshot
            : null;

    /// <summary>The remote player's position, moved on locally between snapshots.</summary>
    public double RemotePosition
    {
        get
        {
            if (RemoteSnapshot is not { } s) return 0;
            if (s.State != nameof(PlaybackState.Playing)) return s.Position;
            var position = s.Position + (DateTime.UtcNow - _snapshotReceivedAt).TotalSeconds;
            var duration = s.Track?.Duration ?? 0;
            return duration > 0 ? Math.Min(position, duration) : position;
        }
    }

    /// <summary>Room, snapshot and connection changes; also every second while a remote player plays.</summary>
    public event Action? Changed;

    /// <summary>A new connection joined the room; the player republishes on it.</summary>
    public event Action? Joined;

    /// <summary>Another tab started playing; this one should pause.</summary>
    public event Action? Superseded;

    /// <summary>Answers commands from controllers. Set by <see cref="RemoteBridge"/>.</summary>
    public Func<RemoteCommand, Task<CommandResult>>? CommandHandler { get; set; }

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./remote.js");

    public async Task InitializeAsync()
    {
        await (_init ??= InitializeCoreAsync());
    }

    private Task? _init;

    private async Task InitializeCoreAsync()
    {
        var module = await ModuleAsync();
        Enabled = await module.InvokeAsync<bool>("isEnabled");
        Me = await module.InvokeAsync<DeviceHello>("identity");
        Initialized = true;
        Changed?.Invoke();
    }

    public async Task SetEnabledAsync(bool on)
    {
        var module = await ModuleAsync();
        await module.InvokeVoidAsync("setEnabled", on);
        Enabled = on;
        if (!on) await DisconnectAsync();
        Changed?.Invoke();
    }

    public async Task<bool> VisibleAsync(DotNetObjectReference<RemoteBridge> bridge) =>
        await (await ModuleAsync()).InvokeAsync<bool>("watchVisibility", bridge);

    public async Task<(double PlayerMs, double ControllerMs)> IdleTimeoutsAsync()
    {
        var t = await (await ModuleAsync()).InvokeAsync<IdleTimeouts>("idleTimeouts");
        return (t.PlayerMs, t.ControllerMs);
    }

    private sealed record IdleTimeouts(double PlayerMs, double ControllerMs);

    /// <summary>
    /// Waits for the room's first roster, so a page can tell whether another device
    /// is playing before it decides anything (PlaybackMemory's restore does).
    /// </summary>
    public async Task<RoomState?> WaitForRoomAsync(TimeSpan timeout)
    {
        await InitializeAsync();
        if (!Enabled) return null;
        await Task.WhenAny(_firstRoom.Task, Task.Delay(timeout));
        return Room;
    }

    public async Task ConnectAsync()
    {
        if (!Enabled || Me is null) return;
        if (_hub is not null && _hub.State != HubConnectionState.Disconnected) return;

        _hub ??= Build();
        try
        {
            await _hub.StartAsync();
            await JoinAsync();
        }
        catch (Exception ex)
        {
            // Not an error banner: no token yet, offline, or a cold start. The
            // bridge tries again on its next tick.
            Console.WriteLine($"[Remote] connect failed: {ex.Message}");
        }
        Changed?.Invoke();
    }

    public async Task DisconnectAsync()
    {
        if (_hub is null) return;
        try { await _hub.StopAsync(); }
        catch (Exception ex) { Console.WriteLine($"[Remote] disconnect: {ex.Message}"); }
        Room = null;
        Changed?.Invoke();
    }

    public async Task PublishAsync(PlayerSnapshot snapshot)
    {
        if (!IsConnected) return;
        try { await _hub!.InvokeAsync("Publish", snapshot); }
        catch (Exception ex) { Console.WriteLine($"[Remote] publish failed: {ex.Message}"); }
    }

    /// <summary>Sends a command to the active player and waits for its answer.</summary>
    public async Task<CommandResult> SendAsync(string kind, double? seconds = null)
    {
        var command = new RemoteCommand(Guid.NewGuid().ToString("N"), kind, seconds);
        if (!IsConnected) return CommandResult.Reject(command.CommandId, "not-connected");
        try { return await _hub!.InvokeAsync<CommandResult>("Send", command); }
        catch (Exception ex) { return CommandResult.Reject(command.CommandId, ex.Message); }
    }

    private HubConnection Build()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_nav.BaseUri), HubPath), options =>
            {
                // WebSockets only, no negotiate round trip: one request lands on
                // one Cloud Run instance (ADR 20261018-63 §2).
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
                // Called on every (re)connect, so each one gets a fresh ticket.
                options.AccessTokenProvider = FetchTicketAsync;
            })
            .WithAutomaticReconnect(new ForeverRetry())
            .Build();

        hub.On<RoomState>("Room", room =>
        {
            SetRoom(room);
            Changed?.Invoke();
        });
        hub.On<string, PlayerSnapshot>("Snapshot", (connectionId, snapshot) =>
        {
            if (Room is null || Room.ActiveConnectionId != connectionId) return;
            SetRoom(Room with { Snapshot = snapshot });
            Changed?.Invoke();
        });
        hub.On("Superseded", () => Superseded?.Invoke());
        hub.On<RemoteCommand, CommandResult>("Command", async command =>
            CommandHandler is { } handler
                ? await handler(command)
                : CommandResult.Reject(command.CommandId, "not-a-player"));

        hub.Reconnecting += _ => { Changed?.Invoke(); return Task.CompletedTask; };
        hub.Reconnected += async _ => await JoinAsync();
        hub.Closed += _ => { Room = null; Changed?.Invoke(); return Task.CompletedTask; };
        return hub;
    }

    private async Task JoinAsync()
    {
        if (_hub is null || Me is null) return;
        var room = await _hub.InvokeAsync<RoomState>("Join", Me);
        SetRoom(room);
        _firstRoom.TrySetResult();
        Joined?.Invoke();
        Changed?.Invoke();
    }

    private void SetRoom(RoomState room)
    {
        Room = room;
        _snapshotReceivedAt = DateTime.UtcNow;
        Tick();
    }

    // While a remote player plays, the bar's clock has to move between snapshots.
    private void Tick()
    {
        if (RemoteSnapshot?.State != nameof(PlaybackState.Playing))
        {
            _ticker?.Cancel();
            _ticker = null;
            return;
        }
        if (_ticker is not null) return;

        var cts = _ticker = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    if (RemoteSnapshot?.State != nameof(PlaybackState.Playing)) break;
                    Changed?.Invoke();
                }
            }
            catch (OperationCanceledException) { }
            if (_ticker == cts) _ticker = null;
        });
    }

    /// <summary>
    /// Trades this tab's Google token for a one-minute ticket. The token goes in a
    /// header, never in a URL; the ticket is what the WebSocket URL carries.
    /// </summary>
    private async Task<string?> FetchTicketAsync()
    {
        string? token;
        try { token = await _js.InvokeAsync<string?>("authGetToken"); }
        catch (JSException) { return null; }
        if (string.IsNullOrEmpty(token)) return null;

        using var http = new HttpClient { BaseAddress = new Uri(_nav.BaseUri) };
        using var request = new HttpRequestMessage(HttpMethod.Post, TicketPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;
        var body = await response.Content.ReadFromJsonAsync<TicketResponse>();
        return body?.Ticket;
    }

    private sealed record TicketResponse(string Ticket);

    /// <summary>Keeps trying: Cloud Run drops every socket at its request timeout by design.</summary>
    private sealed class ForeverRetry : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays =
            [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

        public TimeSpan? NextRetryDelay(RetryContext context) =>
            context.PreviousRetryCount < Delays.Length ? Delays[context.PreviousRetryCount] : TimeSpan.FromSeconds(10);
    }

    public async ValueTask DisposeAsync()
    {
        _ticker?.Cancel();
        if (_hub is not null) await _hub.DisposeAsync();
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
