using System.Collections.Concurrent;

using TraktorGoogleDrive.Models;

namespace TraktorGoogleDrive.Server.Remote;

/// <summary>
/// Rooms, in memory only (ADR 20261018-63 §1). Nothing here needs to survive a
/// restart: the player republishes its full state when it reconnects, so losing
/// this is a few seconds of "Looking for…", never lost playback.
/// </summary>
public sealed class RemoteRooms
{
    /// <summary>Tabs per room. A bound, not a feature: one person has a few devices.</summary>
    public const int MaxMembers = 20;

    private readonly ConcurrentDictionary<string, Room> _rooms = new();

    public Room For(string userKey) => _rooms.GetOrAdd(userKey, _ => new Room());

    public void DropIfEmpty(string userKey)
    {
        if (_rooms.TryGetValue(userKey, out var room) && room.IsEmpty)
            _rooms.TryRemove(new KeyValuePair<string, Room>(userKey, room));
    }

    public sealed class Room
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Member> _members = [];
        private long _order;

        public string? ActiveConnectionId { get; private set; }

        private sealed class Member(DeviceHello hello)
        {
            public DeviceHello Hello { get; } = hello;
            public PlayerSnapshot? Last { get; set; }
            public long PublishedAt { get; set; }
        }

        public bool IsEmpty { get { lock (_gate) return _members.Count == 0; } }

        public bool Join(string connectionId, DeviceHello hello)
        {
            lock (_gate)
            {
                if (_members.Count >= MaxMembers && !_members.ContainsKey(connectionId)) return false;
                _members[connectionId] = new Member(hello);
                return true;
            }
        }

        /// <summary>Removes a tab. True when the active player changed.</summary>
        public bool Leave(string connectionId)
        {
            lock (_gate)
            {
                _members.Remove(connectionId);
                if (ActiveConnectionId != connectionId) return false;
                ActiveConnectionId = NextActive();
                return true;
            }
        }

        public DeviceHello? HelloOf(string connectionId)
        {
            lock (_gate) return _members.TryGetValue(connectionId, out var m) ? m.Hello : null;
        }

        /// <summary>
        /// Records a snapshot. A tab that starts playing takes the active role
        /// (last press wins, §4.4); <paramref name="superseded"/> is the tab it
        /// took it from, which is told to pause. A paused tab becomes active only
        /// if nobody is. A tab that unloads gives the role to whoever is next.
        /// </summary>
        public bool Publish(string connectionId, PlayerSnapshot snapshot, out string? superseded)
        {
            superseded = null;
            lock (_gate)
            {
                if (!_members.TryGetValue(connectionId, out var member)) return false;
                var before = ActiveConnectionId;
                member.Last = snapshot;
                member.PublishedAt = ++_order;

                var loaded = snapshot.Track is not null;
                var playing = snapshot.State is "Playing" or "Loading";

                if (!loaded)
                {
                    member.Last = null;
                    if (ActiveConnectionId == connectionId) ActiveConnectionId = NextActive();
                }
                else if (playing && ActiveConnectionId != connectionId)
                {
                    if (ActiveConnectionId is { } previous && IsPlaying(previous)) superseded = previous;
                    ActiveConnectionId = connectionId;
                }
                else if (ActiveConnectionId is null)
                {
                    ActiveConnectionId = connectionId;
                }

                return before != ActiveConnectionId;
            }
        }

        public RoomState State()
        {
            lock (_gate)
            {
                var devices = _members
                    .Select(kv => new DeviceInfo(kv.Key, kv.Value.Hello.DeviceId, kv.Value.Hello.Name,
                        kv.Key == ActiveConnectionId))
                    .ToList();
                var snapshot = ActiveConnectionId is { } id && _members.TryGetValue(id, out var m) ? m.Last : null;
                return new RoomState(devices, ActiveConnectionId, snapshot);
            }
        }

        private bool IsPlaying(string connectionId) =>
            _members.TryGetValue(connectionId, out var m) && m.Last?.State is "Playing" or "Loading";

        // Caller holds the lock. A playing tab first, then the most recent one
        // with something loaded.
        private string? NextActive() =>
            _members
                .Where(kv => kv.Value.Last?.Track is not null)
                .OrderByDescending(kv => kv.Value.Last!.State is "Playing" or "Loading")
                .ThenByDescending(kv => kv.Value.PublishedAt)
                .Select(kv => kv.Key)
                .FirstOrDefault();
    }
}
