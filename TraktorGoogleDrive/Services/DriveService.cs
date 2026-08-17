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

public class DriveRequestException : Exception
{
    public DriveRequestException(string message) : base(message) { }
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

    /// <summary>
    /// Google puts the useful part of a failure in the response body ("File not
    /// found", "insufficientPermissions", …), so surface it rather than just a
    /// bare status code.
    /// </summary>
    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode) return;

        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync();
            if (body.Length > 400) body = body[..400] + "…";
        }
        catch
        {
            // Body unavailable — the status alone still has to be reported.
        }

        var message = $"{what}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}"
                    + (string.IsNullOrWhiteSpace(body) ? "" : $" — {body}");

        throw response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? new DriveAuthException(message)
            : new DriveRequestException(message);
    }

    private async Task<JsonElement> SendAsync(string url, string token)
    {
        var response = await _http.SendAsync(Get(url, token));
        await ThrowIfFailedAsync(response, "Drive query failed");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<string> DownloadTextAsync(string fileId, string token)
    {
        var response = await _http.SendAsync(
            Get($"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media", token));
        await ThrowIfFailedAsync(response, $"Downloading Drive file {fileId} failed");
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

    public sealed record FileMeta(string Id, string Name, DateTimeOffset? ModifiedTime);

    /// <summary>
    /// When Drive last received a new version of this file — i.e. when Traktor
    /// last synced it. One small call; the download itself carries no usable
    /// modification time.
    /// </summary>
    public async Task<FileMeta?> GetMetadataAsync(string fileId, string token)
    {
        try
        {
            var json = await SendAsync(
                $"https://www.googleapis.com/drive/v3/files/{fileId}?fields=id,name,modifiedTime", token);
            return new FileMeta(
                json.TryGetProperty("id", out var i) ? i.GetString() ?? fileId : fileId,
                json.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                json.TryGetProperty("modifiedTime", out var m) && m.GetString() is { } s
                    ? DateTimeOffset.Parse(s)
                    : null);
        }
        catch (DriveAuthException) { throw; }
        catch { return null; } // a missing timestamp must not fail the load
    }

    public sealed record NamedFile(string Id, string Name, DateTimeOffset? ModifiedTime)
    {
        /// <summary>Name of the containing Drive folder, once resolved.</summary>
        public string? FolderName { get; init; }

        /// <summary>Version parsed from a "Traktor 4.4.1" style folder name.</summary>
        public Version? TraktorVersion { get; init; }

        /// <summary>Sitting in a Backup/Crashlogs style folder.</summary>
        public bool IsBackup { get; init; }

        public string Describe() =>
            $"{FolderName ?? "(folder lookup failed)"}/{Name}"
            + (TraktorVersion is null ? "" : $"  [Traktor {TraktorVersion}]")
            + (IsBackup ? "  [backup]" : "")
            + $"  modified {ModifiedTime:yyyy-MM-dd}";
    }

    private static readonly System.Text.RegularExpressions.Regex TraktorFolderPattern =
        new(@"traktor\D*(\d+)(?:\.(\d+))?(?:\.(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static Version? ParseTraktorVersion(string? folderName)
    {
        if (folderName is null) return null;
        var m = TraktorFolderPattern.Match(folderName);
        if (!m.Success) return null;
        return new Version(
            int.Parse(m.Groups[1].Value),
            m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0,
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    private static bool LooksLikeBackup(string? folderName) =>
        folderName is not null
        && (folderName.Contains("backup", StringComparison.OrdinalIgnoreCase)
         || folderName.Contains("crashlog", StringComparison.OrdinalIgnoreCase)
         || folderName.Contains("recovery", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds every file with this name and annotates each with its containing
    /// folder, ordered best-first.
    /// </summary>
    /// <remarks>
    /// Ordering by modifiedTime alone is wrong for Traktor: a Backup copy is
    /// often touched more recently than the live collection, and an install
    /// leaves one collection.nml per version (Traktor 3.x, 4.0, 4.4.1 …). The
    /// live one is the file under the highest-versioned "Traktor N.N.N" folder,
    /// so rank by that first and fall back to modified time.
    /// </remarks>
    public async Task<List<NamedFile>> FindFilesNamedAsync(string name, string token)
    {
        var query = $"name = '{name.Replace("'", "\\'")}' and trashed = false";
        var url = "https://www.googleapis.com/drive/v3/files"
                + $"?q={Uri.EscapeDataString(query)}"
                + "&fields=files(id,name,modifiedTime,parents)&orderBy=modifiedTime desc&pageSize=100";

        var json = await SendAsync(url, token);
        if (!json.TryGetProperty("files", out var files)) return [];

        var raw = files.EnumerateArray().Select(f => (
            Id: f.GetProperty("id").GetString()!,
            Name: f.GetProperty("name").GetString()!,
            Modified: f.TryGetProperty("modifiedTime", out var m) && m.GetString() is { } s
                ? DateTimeOffset.Parse(s)
                : (DateTimeOffset?)null,
            Parent: f.TryGetProperty("parents", out var p)
                ? p.EnumerateArray().Select(x => x.GetString()!).FirstOrDefault()
                : null)).ToList();

        var annotated = new List<NamedFile>(raw.Count);
        foreach (var r in raw)
        {
            var folder = r.Parent is null ? null : await FolderNameAsync(r.Parent, token);
            annotated.Add(new NamedFile(r.Id, r.Name, r.Modified)
            {
                FolderName = folder,
                TraktorVersion = ParseTraktorVersion(folder),
                IsBackup = LooksLikeBackup(folder),
            });
        }

        return annotated
            .OrderBy(f => f.IsBackup)
            .ThenByDescending(f => f.TraktorVersion ?? new Version(0, 0, 0))
            .ThenByDescending(f => f.ModifiedTime ?? DateTimeOffset.MinValue)
            .ToList();
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
