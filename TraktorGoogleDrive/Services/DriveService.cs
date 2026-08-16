using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using TraktorGoogleDrive.Models;

using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Services;

public class DriveAuthException : Exception
{
    public DriveAuthException(string message) : base(message) { }
}

public class DriveService
{
    // Drive rejects very long `q` strings, and a set can hold hundreds of
    // tracks, so name lookups go out in batches rather than one giant OR chain.
    private const int NamesPerQuery = 40;

    private readonly HttpClient _http;
    private readonly Dictionary<string, string?> _folderNames = new();

    public DriveService(HttpClient http) => _http = http;

    private static HttpRequestMessage Get(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Header, not ?access_token= — a query param lands in server logs and
        // browser history.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<JsonElement> SendAsync(string url, string token)
    {
        var response = await _http.SendAsync(Get(url, token));
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new DriveAuthException($"Drive returned {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> DownloadTextAsync(string fileId, string token)
    {
        var response = await _http.SendAsync(
            Get($"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media", token));
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new DriveAuthException($"Drive returned {(int)response.StatusCode}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private sealed record DriveFile(string Id, string Name, string MimeType, string[] Parents);

    private async Task<List<DriveFile>> FindByNamesAsync(IEnumerable<string> names, string token)
    {
        var found = new List<DriveFile>();

        foreach (var batch in names.Distinct().Chunk(NamesPerQuery))
        {
            // Drive's query grammar escapes a literal quote as \' .
            var conditions = string.Join(" or ", batch.Select(n => $"name = '{n.Replace("'", "\\'")}'"));
            var query = $"mimeType contains 'audio/' and ({conditions})";
            var url = "https://www.googleapis.com/drive/v3/files"
                    + $"?q={Uri.EscapeDataString(query)}"
                    + "&fields=files(id,name,mimeType,parents)&pageSize=1000";

            var json = await SendAsync(url, token);
            if (!json.TryGetProperty("files", out var files)) continue;

            found.AddRange(files.EnumerateArray().Select(f => new DriveFile(
                f.GetProperty("id").GetString()!,
                f.GetProperty("name").GetString()!,
                f.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? "" : "",
                f.TryGetProperty("parents", out var p)
                    ? p.EnumerateArray().Select(x => x.GetString()!).ToArray()
                    : [])));
        }

        return found;
    }

    private async Task<string?> FolderNameAsync(string folderId, string token)
    {
        if (_folderNames.TryGetValue(folderId, out var cached)) return cached;
        try
        {
            var json = await SendAsync(
                $"https://www.googleapis.com/drive/v3/files/{folderId}?fields=name", token);
            var name = json.TryGetProperty("name", out var n) ? n.GetString() : null;
            return _folderNames[folderId] = name;
        }
        catch (DriveAuthException)
        {
            throw;
        }
        catch
        {
            return _folderNames[folderId] = null;
        }
    }

    /// <summary>
    /// Resolves each track to its Drive file. Returns one FileEntry per track —
    /// never a shared instance, because two tracks can legitimately share a
    /// filename and aliasing them silently destroys one track's metadata.
    /// </summary>
    public async Task<List<FileEntry>> ResolveTracksAsync(IReadOnlyList<Track> tracks, string token)
    {
        var names = tracks
            .Select(t => Path.GetFileName(t.Path))
            .Where(n => !string.IsNullOrEmpty(n));

        var driveFiles = await FindByNamesAsync(names, token);
        var byName = driveFiles.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                               .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var result = new List<FileEntry>(tracks.Count);

        foreach (var track in tracks)
        {
            var entry = new FileEntry { Track = track };
            var fileName = Path.GetFileName(track.Path);

            if (!string.IsNullOrEmpty(fileName) && byName.TryGetValue(fileName, out var candidates))
            {
                var match = candidates.Count == 1
                    ? candidates[0]
                    : await DisambiguateAsync(candidates, track, token);

                if (match is not null)
                {
                    entry.DriveFileId = match.Id;
                    entry.DriveFileName = match.Name;
                    entry.DriveFileMimeType = match.MimeType;
                }
            }

            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Several Drive files share this filename. Traktor knows the containing
    /// folder, so prefer the candidate whose Drive parent folder has the same
    /// name as the track's directory.
    /// </summary>
    private async Task<DriveFile?> DisambiguateAsync(List<DriveFile> candidates, Track track, string token)
    {
        var wantedFolder = Path.GetFileName(Path.GetDirectoryName(track.Path) ?? "");
        if (string.IsNullOrEmpty(wantedFolder)) return candidates[0];

        foreach (var candidate in candidates)
        {
            foreach (var parent in candidate.Parents)
            {
                var name = await FolderNameAsync(parent, token);
                if (string.Equals(name, wantedFolder, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }

        return candidates[0];
    }
}
