using Microsoft.AspNetCore.SignalR;

using TraktorGoogleDrive.Models;

namespace TraktorGoogleDrive.Server.Remote;

/// <summary>
/// Relays between the tabs of one Google account (ADR 20261018-63). The room is
/// never chosen by the client: it is the user key from the ticket, bound when
/// the connection is made (<see cref="RemoteEndpoints"/>), so a connection can
/// only ever reach its own user's devices.
///
/// Client methods: <c>Room(RoomState)</c>, <c>Snapshot(connectionId, PlayerSnapshot)</c>,
/// <c>Superseded()</c>, and <c>Command(RemoteCommand) → CommandResult</c> (a client result).
/// </summary>
public sealed class RemoteHub(RemoteRooms rooms, ILogger<RemoteHub> log) : Hub
{
    public const string UserKeyItem = "remote:userKey";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private string UserKey => (string)Context.Items[UserKeyItem]!;
    private RemoteRooms.Room Room => rooms.For(UserKey);

    public override async Task OnConnectedAsync()
    {
        // Set by the ticket check in front of the hub; absent means the request
        // got here some other way.
        if (Context.GetHttpContext()?.Items[UserKeyItem] is not string userKey)
        {
            Context.Abort();
            return;
        }
        Context.Items[UserKeyItem] = userKey;
        await Groups.AddToGroupAsync(Context.ConnectionId, userKey);
    }

    public async Task<RoomState> Join(DeviceHello hello)
    {
        if (!Valid(hello.DeviceId, 64) || !Valid(hello.TabId, 64) || !Valid(hello.Name, 80))
            throw new HubException("Invalid device");
        if (!Room.Join(Context.ConnectionId, hello))
            throw new HubException("Too many devices in this room");

        log.LogInformation("Remote: {Device} joined room {Room}", hello.Name, UserKey[..8]);
        var state = Room.State();
        await Clients.OthersInGroup(UserKey).SendAsync("Room", state);
        return state;
    }

    public async Task Publish(PlayerSnapshot snapshot)
    {
        var hello = Room.HelloOf(Context.ConnectionId) ?? throw new HubException("Join first");
        if (snapshot.V != PlayerSnapshot.Version) throw new HubException("Unsupported snapshot version");
        if (snapshot.Track is { } t && (!Valid(t.FileId, 200) || (t.Title?.Length ?? 0) > 500))
            throw new HubException("Invalid track");

        // Who published is what the connection says, not what the payload claims.
        snapshot = snapshot with { DeviceId = hello.DeviceId, DeviceName = hello.Name };

        var activeChanged = Room.Publish(Context.ConnectionId, snapshot, out var superseded);
        if (superseded is not null) await Clients.Client(superseded).SendAsync("Superseded");

        if (activeChanged) await Clients.Group(UserKey).SendAsync("Room", Room.State());
        else if (Room.ActiveConnectionId == Context.ConnectionId)
            await Clients.OthersInGroup(UserKey).SendAsync("Snapshot", Context.ConnectionId, snapshot);
    }

    /// <summary>Forwards a command to the active player and returns its answer.</summary>
    public async Task<CommandResult> Send(RemoteCommand command)
    {
        if (!RemoteCommandKind.All.Contains(command.Kind) || !Valid(command.CommandId, 64))
            return CommandResult.Reject(command.CommandId ?? "", "unknown-command");
        if (command.Seconds is { } s && (!double.IsFinite(s) || s < 0))
            return CommandResult.Reject(command.CommandId, "invalid-position");

        var target = Room.ActiveConnectionId;
        if (target is null || target == Context.ConnectionId)
            return CommandResult.Reject(command.CommandId, "no-player");

        try
        {
            using var timeout = new CancellationTokenSource(CommandTimeout);
            return await Clients.Client(target).InvokeAsync<CommandResult>("Command", command, timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or HubException)
        {
            return CommandResult.Reject(command.CommandId, "player-unreachable");
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items[UserKeyItem] is not string userKey) return;
        var room = rooms.For(userKey);
        room.Leave(Context.ConnectionId);
        await Clients.Group(userKey).SendAsync("Room", room.State());
        rooms.DropIfEmpty(userKey);
    }

    private static bool Valid(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
}
