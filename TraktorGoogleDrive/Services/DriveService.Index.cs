using System.Diagnostics;
using System.Text.Json;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Every audio file and folder in Drive, by name, held in memory and kept in
/// IndexedDB between visits.
/// </summary>
/// <remarks>
/// Without it, opening a playlist asked Drive for its tracks by name, 40 names
/// per files.list, one after the other: 5 round trips for a 200-entry set, on
/// every switch, ~0.9 s on a good line and 2+ s on a train. With it a switch
/// makes no Drive call at all.
///
/// Built once with a paged files.list over all audio files (and all folders,
/// whose names pick between same-named files). Afterwards each app load asks
/// Drive's change feed what happened since the stored page token, which is one
/// small call when nothing changed. The stored index is dropped, and rebuilt,
/// when it belongs to another account, is older than a week (a safety net for
/// anything the change feed does not report), or its format changes.
/// </remarks>
public partial class DriveService
{
    private const string IndexKey = "drive-index:v1";
    private const string FolderMime = "application/vnd.google-apps.folder";
    private static readonly TimeSpan IndexMaxAge = TimeSpan.FromDays(7);

    private readonly Dictionary<string, DriveFile> _audioById = new(StringComparer.Ordinal);
    private Dictionary<string, List<DriveFile>> _audioByName = new(StringComparer.OrdinalIgnoreCase);

    private Task? _storedIndexLoad;
    private Task<bool>? _indexReady;
    private string? _pageToken;
    private string? _permissionId;
    private DateTimeOffset _builtAt;

    /// <summary>
    /// Starts loading the index (from IndexedDB, then brought up to date from
    /// Drive) without waiting for it. Called at app start, so it runs alongside
    /// the collection download rather than after it.
    /// </summary>
    public void StartIndex(string token) => _ = EnsureIndexAsync(token);

    /// <summary>True when every name lookup can be answered from memory.</summary>
    private async Task<bool> EnsureIndexAsync(string token)
    {
        var task = _indexReady ??= BuildOrRefreshIndexAsync(token);
        try
        {
            return await task;
        }
        catch (DriveAuthException)
        {
            _indexReady = null; // try again with the next token
            throw;
        }
    }

    /// <summary>Only the IndexedDB read, so a folder name lookup can use it before Drive answers.</summary>
    private Task LoadStoredIndexAsync() => _storedIndexLoad ??= ReadStoredIndexAsync();

    private async Task<bool> BuildOrRefreshIndexAsync(string token)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            await LoadStoredIndexAsync();
            var about = AboutPermissionIdAsync(token);

            if (_pageToken is not null && DateTimeOffset.UtcNow - _builtAt < IndexMaxAge)
            {
                // Speculatively in parallel with the account check: one round trip, not two.
                var changes = ReadChangesAsync(_pageToken, token);
                if (await about == _permissionId)
                {
                    var (applied, next) = await changes;
                    var moved = next != _pageToken;
                    _pageToken = next;
                    if (applied > 0) RebuildNameMap();
                    if (applied > 0 || moved) await SaveIndexAsync();
                    Perf.Log("index.refresh", watch, $"{_audioById.Count} files, {applied} changes");
                    return true;
                }
                // Another account's index: let its speculative refresh finish, so it
                // cannot write into the rebuilt one, then start over.
                try { await changes; } catch (Exception) { /* discarded anyway */ }
            }

            await BuildIndexAsync(token, about);
            Perf.Log("index.build", watch, $"{_audioById.Count} files, {_folderNames.Count} folders");
            return true;
        }
        catch (DriveAuthException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not fatal: playlists fall back to asking Drive by name, as before.
            Console.WriteLine($"[DriveIndex] unavailable, falling back to name lookups: {ex.Message}");
            return false;
        }
    }

    private async Task BuildIndexAsync(string token, Task<string?> about)
    {
        // The start token first: anything that changes while the listing runs is
        // then still reported by the next refresh.
        var start = await SendAsync(
            "https://www.googleapis.com/drive/v3/changes/startPageToken?fields=startPageToken", token);
        var pageToken = start.GetProperty("startPageToken").GetString();

        var audio = ListAllAsync("mimeType contains 'audio/' and trashed = false", "id,name,mimeType,parents", token);
        var folders = ListAllAsync($"mimeType = '{FolderMime}' and trashed = false", "id,name", token);

        _audioById.Clear();
        foreach (var f in await audio) _audioById[f.Id] = f;
        foreach (var f in await folders) _folderNames[f.Id] = f.Name;
        var cpu = Stopwatch.StartNew();

        _permissionId = await about;
        _pageToken = pageToken;
        _builtAt = DateTimeOffset.UtcNow;
        RebuildNameMap();
        Perf.Log("index.build.map", cpu);
        await SaveIndexAsync();
        Perf.Log("index.build.save", cpu);
    }

    private async Task<List<DriveFile>> ListAllAsync(string query, string fields, string token)
    {
        var all = new List<DriveFile>();
        string? next = null;
        do
        {
            var url = "https://www.googleapis.com/drive/v3/files"
                    + $"?q={Uri.EscapeDataString(query)}"
                    + $"&fields=nextPageToken,files({fields})&pageSize=1000"
                    + (next is null ? "" : $"&pageToken={Uri.EscapeDataString(next)}");
            var json = await SendAsync(url, token);
            if (json.TryGetProperty("files", out var files))
                all.AddRange(files.EnumerateArray().Select(ReadFile));
            next = json.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        } while (next is not null);
        return all;
    }

    private async Task<string?> AboutPermissionIdAsync(string token)
    {
        var json = await SendAsync("https://www.googleapis.com/drive/v3/about?fields=user(permissionId)", token);
        return json.TryGetProperty("user", out var u) && u.TryGetProperty("permissionId", out var p) ? p.GetString() : null;
    }

    /// <summary>Applies Drive's change feed since <paramref name="pageToken"/>. Returns how many changes touched the index.</summary>
    private async Task<(int Applied, string NextToken)> ReadChangesAsync(string pageToken, string token)
    {
        var applied = 0;
        var next = pageToken;
        while (true)
        {
            var url = "https://www.googleapis.com/drive/v3/changes"
                    + $"?pageToken={Uri.EscapeDataString(next)}&pageSize=1000&includeRemoved=true"
                    + "&fields=nextPageToken,newStartPageToken,changes(fileId,removed,file(id,name,mimeType,parents,trashed))";
            var json = await SendAsync(url, token);

            var changes = json.TryGetProperty("changes", out var cs) ? cs.EnumerateArray().ToList() : [];
            foreach (var change in changes)
            {
                var id = change.TryGetProperty("fileId", out var fid) ? fid.GetString() : null;
                if (id is null) continue;

                var removed = change.TryGetProperty("removed", out var r) && r.GetBoolean();
                var hasFile = change.TryGetProperty("file", out var file);
                var trashed = hasFile && file.TryGetProperty("trashed", out var t) && t.GetBoolean();
                var gone = removed || !hasFile || trashed;

                if (_audioById.Remove(id)) applied++;
                if (gone) continue;

                var entry = ReadFile(file);
                if (entry.MimeType == FolderMime)
                {
                    _folderNames[entry.Id] = entry.Name; // a renamed folder
                }
                else if (entry.MimeType.StartsWith("audio/", StringComparison.Ordinal))
                {
                    _audioById[entry.Id] = entry;
                    applied++;
                }
            }

            if (json.TryGetProperty("nextPageToken", out var np) && np.GetString() is { } more) { next = more; continue; }
            return (applied, json.TryGetProperty("newStartPageToken", out var ns) ? ns.GetString() ?? next : next);
        }
    }

    private static DriveFile ReadFile(JsonElement f) => new(
        f.GetProperty("id").GetString()!,
        f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
        f.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "",
        f.TryGetProperty("parents", out var p) ? p.EnumerateArray().Select(x => x.GetString()!).ToArray() : []);

    private void RebuildNameMap() =>
        _audioByName = _audioById.Values
            .GroupBy(f => Nfc(f.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    // Stored compactly, [id, name, mimeType, parent, parent…] per file: JSON of
    // a few thousand of these is what every load reads back.
    private sealed record StoredIndex(
        int V, string? PermissionId, string PageToken, DateTimeOffset BuiltAt,
        List<string[]> Files, Dictionary<string, string> Folders);

    private async Task ReadStoredIndexAsync()
    {
        var watch = Stopwatch.StartNew();
        var raw = await _cache.GetBytesAsync(IndexKey);
        if (raw is not { Length: > 0 }) return;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredIndex>(raw);
            if (stored is not { V: 1 }) return;

            foreach (var f in stored.Files.Where(f => f.Length >= 3))
                _audioById[f[0]] = new DriveFile(f[0], f[1], f[2], f[3..]);
            foreach (var (id, name) in stored.Folders) _folderNames.TryAdd(id, name);

            _permissionId = stored.PermissionId;
            _pageToken = stored.PageToken;
            _builtAt = stored.BuiltAt;
            RebuildNameMap();
            Perf.Log("index.stored", watch, $"{_audioById.Count} files");
        }
        catch (JsonException)
        {
            // Unreadable: rebuilt from Drive.
        }
    }

    private Task SaveIndexAsync()
    {
        if (_pageToken is null) return Task.CompletedTask;
        var stored = new StoredIndex(
            1, _permissionId, _pageToken, _builtAt,
            _audioById.Values.Select(f => (string[])[f.Id, f.Name, f.MimeType, .. f.Parents]).ToList(),
            _folderNames.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!));
        return _cache.SetBytesAsync(IndexKey, JsonSerializer.SerializeToUtf8Bytes(stored));
    }

    /// <summary>
    /// Folder names learned one at a time (disambiguation, collection.nml
    /// discovery) go into the stored index too, so the next load knows them.
    /// Once per batch of lookups, not per folder.
    /// </summary>
    private bool _folderNamesDirty;

    private void SaveLearnedFolderNames()
    {
        if (!_folderNamesDirty || _indexReady is not { IsCompletedSuccessfully: true, Result: true }) return;
        _folderNamesDirty = false;
        _ = SaveIndexAsync();
    }
}
