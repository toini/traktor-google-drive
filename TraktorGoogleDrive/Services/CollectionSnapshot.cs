using System.Text.Json;
using System.Text.Json.Serialization;

using TraktorGoogleDrive.Models;

using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// The parsed collection.nml as JSON, so a reload skips the XML altogether.
/// </summary>
/// <remarks>
/// Parsing the XML was the largest single cost of every app start: ~1.2 s on a
/// laptop and ~5 s with a tablet's CPU for 1,600 tracks, every time, even when
/// Traktor had not synced since. Reading this back is several times cheaper.
/// Tracks are stored once and referred to by index, as the parser shares one
/// Track between the collection, its playlists and their entries; the rebuilt
/// graph shares them the same way.
/// </remarks>
public sealed class CollectionSnapshot
{
    /// <summary>Bump when the shape or what the app derives from the NML changes; an old one is then a miss.</summary>
    /// <remarks>2: <see cref="ExtrasData"/> carries PLAYCOUNT and LAST_PLAYED, for track discovery's seeds.</remarks>
    public const int CurrentVersion = 2;

    public int V { get; set; }
    public string FileId { get; set; } = "";
    public DateTimeOffset ModifiedTime { get; set; }

    /// <summary>Every Track the app holds: the collection's, then any only an entry refers to.</summary>
    public List<Track> Tracks { get; set; } = [];

    /// <summary>How many of <see cref="Tracks"/> are Collection.Tracks.</summary>
    public int CollectionTrackCount { get; set; }

    public List<FolderData> Folders { get; set; } = [];
    public Dictionary<string, List<EntryData>> Entries { get; set; } = [];

    /// <summary>COLOR, RANKING and ALBUM, which the parser does not read, by track index.</summary>
    public Dictionary<int, ExtrasData> Extras { get; set; } = [];

    public sealed record FolderData(string? Name, List<FolderData> Folders, List<PlaylistData> Playlists);

    public sealed record PlaylistData(string Uuid, string Name, int Entries, int[] Tracks);

    /// <summary>One playlist ENTRY. T is the track index, -1 for none.</summary>
    public sealed record EntryData(int P, string K, EntryKind Kind, int T, string Title);

    public sealed record ExtrasData(int? Color, int? Ranking, string? Album, int PlayCount = 0, string? LastPlayed = null);

    public static CollectionSnapshot Capture(
        string fileId, DateTimeOffset modified, Collection collection,
        IReadOnlyDictionary<string, List<PlaylistEntry>> entries,
        IReadOnlyDictionary<string, ExtrasData> extrasByPath)
    {
        var tracks = new List<Track>(collection.Tracks);
        var index = new Dictionary<Track, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < tracks.Count; i++) index.TryAdd(tracks[i], i);

        int IndexOf(Track? t)
        {
            if (t is null) return -1;
            if (index.TryGetValue(t, out var i)) return i;
            tracks.Add(t);
            return index[t] = tracks.Count - 1;
        }

        FolderData Folder(Folder f) => new(
            f.Name,
            f.Folders.Select(Folder).ToList(),
            f.Playlists.Select(p => new PlaylistData(p.Uuid, p.Name, p.Entries, p.Tracks.Select(IndexOf).ToArray())).ToList());

        var snapshot = new CollectionSnapshot
        {
            V = CurrentVersion,
            FileId = fileId,
            ModifiedTime = modified,
            CollectionTrackCount = collection.Tracks.Count,
            Folders = collection.Folders.Select(Folder).ToList(),
            Entries = entries.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(e => new EntryData(e.Position, e.Key, e.Kind, IndexOf(e.Track), e.Title)).ToList()),
        };

        for (var i = 0; i < tracks.Count; i++)
            if (extrasByPath.TryGetValue(tracks[i].Path, out var x)) snapshot.Extras[i] = x;

        snapshot.Tracks = tracks;
        return snapshot;
    }

    public (Collection Collection, Dictionary<string, List<PlaylistEntry>> Entries, Dictionary<string, ExtrasData> ExtrasByPath) Restore()
    {
        Track? At(int i) => i >= 0 && i < Tracks.Count ? Tracks[i] : null;

        Folder Folder(FolderData f) => new()
        {
            Name = f.Name,
            Folders = f.Folders.Select(Folder).ToList(),
            Playlists = f.Playlists.Select(p => new Playlist
            {
                Uuid = p.Uuid,
                Name = p.Name,
                Entries = p.Entries,
                Tracks = p.Tracks.Select(At).OfType<Track>().ToList(),
            }).ToList(),
        };

        var collection = new Collection
        {
            Tracks = Tracks.Take(CollectionTrackCount).ToList(),
            Folders = Folders.Select(Folder).ToList(),
        };

        var entries = Entries.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(e => new PlaylistEntry(e.P, e.K, e.Kind, At(e.T), e.Title)).ToList(),
            StringComparer.Ordinal);

        var extras = new Dictionary<string, ExtrasData>();
        foreach (var (i, x) in Extras)
            if (At(i) is { Path.Length: > 0 } t) extras[t.Path] = x;

        return (collection, entries, extras);
    }

    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, SnapshotJson.Default.CollectionSnapshot);

    public static CollectionSnapshot? FromUtf8(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize(bytes, SnapshotJson.Default.CollectionSnapshot); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

// Source-generated: reflection-based (de)serialization of a few thousand
// objects is markedly slower in WebAssembly's interpreter.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CollectionSnapshot))]
internal partial class SnapshotJson : JsonSerializerContext;
