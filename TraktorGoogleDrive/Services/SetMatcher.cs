using Microsoft.JSInterop;

using TraktorGoogleDrive.Models;

using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Links a recording to the playlists that were built for the same set.
/// The heuristic gets most of them; the user's explicit choice always wins and
/// is remembered, because ~1 in 5 is genuinely ambiguous or has no playlist.
/// </summary>
public class SetMatcher
{
    private const string StoragePrefix = "set-match:";

    private readonly CollectionService _collection;
    private readonly IJSRuntime _js;

    private Dictionary<string, List<Playlist>>? _families;

    public SetMatcher(CollectionService collection, IJSRuntime js)
    {
        _collection = collection;
        _js = js;
    }

    private async Task<Dictionary<string, List<Playlist>>> FamiliesAsync()
    {
        if (_families is not null) return _families;

        var all = (await _collection.GetCollectionAsync()).Folders
            .SelectMany(f => f.Playlists)
            .Where(p => p.Name != "Recorded sets")
            .ToList();

        _families = all
            .GroupBy(p => SetName.Key(p.Name))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(
                g => g.Key,
                // .rec first, then shortest name — the base set before its variants.
                g => g.OrderBy(p => SetName.IsRecorded(p.Name) ? 0 : 1)
                      .ThenBy(p => p.Name.Length)
                      .ToList());
        return _families;
    }

    /// <summary>Playlists plausibly belonging to the same set as this recording, best first.</summary>
    public async Task<IReadOnlyList<Playlist>> CandidatesAsync(string recordingFileName)
    {
        var families = await FamiliesAsync();
        var key = SetName.Key(recordingFileName);
        if (key.Length == 0) return [];

        if (families.TryGetValue(key, out var exact)) return exact;

        // Fall back to a shared prefix, closest first — "H15" finding
        // "H15 2023 Darker Sounds".
        return families
            .Where(kv => kv.Key.StartsWith(key, StringComparison.Ordinal)
                      || key.StartsWith(kv.Key, StringComparison.Ordinal))
            .OrderBy(kv => Math.Abs(kv.Key.Length - key.Length))
            .SelectMany(kv => kv.Value)
            .ToList();
    }

    /// <summary>The playlist to show: the user's pick if they made one, else the best guess.</summary>
    public async Task<Playlist?> ResolveAsync(string recordingFileName, string recordingFileId)
    {
        var candidates = await CandidatesAsync(recordingFileName);
        var chosen = await GetOverrideAsync(recordingFileId);

        if (chosen is not null)
        {
            var pinned = (await _collection.GetCollectionAsync()).Folders
                .SelectMany(f => f.Playlists)
                .FirstOrDefault(p => p.Uuid == chosen);
            if (pinned is not null) return pinned;
        }

        return candidates.FirstOrDefault();
    }

    public async Task<string?> GetOverrideAsync(string recordingFileId)
    {
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", StoragePrefix + recordingFileId); }
        catch { return null; }
    }

    public async Task SetOverrideAsync(string recordingFileId, string? playlistUuid)
    {
        try
        {
            if (string.IsNullOrEmpty(playlistUuid))
                await _js.InvokeVoidAsync("localStorage.removeItem", StoragePrefix + recordingFileId);
            else
                await _js.InvokeVoidAsync("localStorage.setItem", StoragePrefix + recordingFileId, playlistUuid);
        }
        catch { /* storage unavailable — the heuristic still works, just not remembered */ }
    }

    /// <summary>Every playlist, for the "none of these" case where the user picks manually.</summary>
    public async Task<IReadOnlyList<Playlist>> AllPlaylistsAsync() =>
        (await _collection.GetCollectionAsync()).Folders
            .SelectMany(f => f.Playlists)
            .Where(p => p.Name != "Recorded sets")
            .OrderBy(p => p.Name)
            .ToList();
}
