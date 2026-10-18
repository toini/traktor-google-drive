namespace TraktorGoogleDrive.Models;

// The remote-control wire protocol (ADR 20261018-63 §1). Shared by the hub in
// TraktorGoogleDrive.Server, which references this project, and the client.
//
// Nothing here may ever carry a Google access token or a URL built from one:
// a controller must never be able to obtain the player's token. Snapshots carry
// ids only; each device builds its own media URLs from its own token.

/// <summary>Sent by every tab when it joins its room.</summary>
public sealed record DeviceHello(string DeviceId, string TabId, string Name);

/// <summary>One connected tab, as the room's roster shows it.</summary>
public sealed record DeviceInfo(string ConnectionId, string DeviceId, string Name, bool IsActive);

/// <summary>The room as the hub sees it: who is connected and who is playing.</summary>
public sealed record RoomState(
    IReadOnlyList<DeviceInfo> Devices,
    string? ActiveConnectionId,
    PlayerSnapshot? Snapshot);

/// <summary>What is loaded on the player. Ids and display text only.</summary>
public sealed record RemoteTrack(
    string FileId,
    string? Title,
    string? PlaylistUuid,
    bool IsWav,
    double Duration);

/// <summary>Gig preview as the player is running it (ADR 20261018-47 §4).</summary>
public sealed record RemotePreview(double Start, double? End, string? Note);

/// <summary>
/// Published by the playing device on every state change, and every 10 s while
/// playing. Controllers interpolate the position in between.
/// </summary>
public sealed record PlayerSnapshot(
    int V,
    long Seq,
    string DeviceId,
    string DeviceName,
    RemoteTrack? Track,
    // A PlaybackState name, or "Blocked" when autoplay was refused.
    string State,
    double Position,
    bool Continuous,
    bool PreviewActive,
    RemotePreview? Preview,
    int QueuePosition,
    int QueueCount,
    // The TV's name while the player is casting, otherwise null.
    string? CastDeviceName,
    // The player's Google session expired: it cannot fetch audio until someone signs in there.
    bool NeedsSignIn)
{
    public const int Version = 1;
}

/// <summary>Command kinds. Absolute where possible, so a retry is harmless.</summary>
public static class RemoteCommandKind
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Seek = "seek";
    public const string Next = "next";
    public const string Previous = "previous";
    public const string RequestSnapshot = "requestSnapshot";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Play, Pause, Seek, Next, Previous, RequestSnapshot };
}

public sealed record RemoteCommand(string CommandId, string Kind, double? Seconds = null);

/// <summary>The player's answer to a command. <c>Reason</c> is shown to the user on a reject.</summary>
public sealed record CommandResult(string CommandId, bool Ok, string? Reason = null)
{
    public static CommandResult Accept(string id) => new(id, true);
    public static CommandResult Reject(string id, string reason) => new(id, false, reason);
}
