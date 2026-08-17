using Microsoft.JSInterop;

using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Services;

public class CollectionService
{
    public const string CollectionFileName = "collection.nml";

    /// <summary>
    /// The id this app shipped with. Kept only as a first guess — it 404'd once
    /// the file changed, so a miss falls through to search rather than failing.
    /// </summary>
    private const string LegacyCollectionFileId = "1yqP8GXUb9qLV8gXRLpvKpyy7DDY7CqAC";

    /// <summary>
    /// Only ever holds a file the USER picked. An automatically resolved id is
    /// deliberately not remembered: a Traktor upgrade creates a new
    /// "Traktor N.N.N/collection.nml" while the old one stays in Drive, so a
    /// cached automatic choice silently pins the app to the pre-upgrade file
    /// forever.
    /// </summary>
    /// v2 deliberately abandons any value written under the old key: that key
    /// held automatically resolved ids too, and there is no way to tell those
    /// from a real user choice after the fact.
    private const string StorageKey = "collection_file_id_v2";

    // Traktor's own root node. It is a container, not a folder the user made,
    // so it should never appear in the sidebar as if it were one.
    private const string TraktorRootNodeName = "$ROOT";

    private readonly DriveService _drive;
    private readonly AuthService _auth;
    private readonly AppErrors _errors;
    private readonly IJSRuntime _js;

    private Collection? _collection;
    private Task<Collection>? _inFlight;

    public CollectionService(DriveService drive, AuthService auth, AppErrors errors, IJSRuntime js)
    {
        _drive = drive;
        _auth = auth;
        _errors = errors;
        _js = js;
    }

    /// <summary>The Drive file id actually used for the last successful load.</summary>
    public string? ResolvedFileId { get; private set; }

    /// <summary>When Drive last received a new collection.nml, i.e. Traktor's last sync.</summary>
    public DateTimeOffset? LastSyncedAt { get; private set; }

    /// <summary>Most recently imported track in the whole collection.</summary>
    public Track? LastAdded { get; private set; }

    /// <summary>Set when several collection.nml files exist and one was chosen.</summary>
    public IReadOnlyList<DriveService.NamedFile> Candidates { get; private set; } = [];

    /// <summary>
    /// The collection is a single large download shared by every page, so
    /// concurrent callers await one request rather than each starting their own.
    /// </summary>
    public Task<Collection> GetCollectionAsync()
    {
        if (_collection is not null) return Task.FromResult(_collection);
        return _inFlight ??= LoadAsync();
    }

    private async Task<Collection> LoadAsync()
    {
        try
        {
            var token = await _auth.GetTokenAsync()
                ?? throw new DriveAuthException("No access token — sign in again.");

            var content = await FetchCollectionTextAsync(token);

            // The old code fed Drive's JSON error body straight into the XML
            // parser, so a 404 surfaced as an unhandled XmlException and a blank
            // page. Check the shape before parsing.
            if (!LooksLikeNml(content))
                throw new DriveRequestException(
                    $"{CollectionFileName} (id {ResolvedFileId}) is not Traktor XML. "
                  + $"First bytes: {Truncate(content, 200)}");

            var parser = new TraktorNmlParser.NmlParser();
            _collection = parser.Load(content);

            LastAdded = _collection.Tracks
                .Where(t => Models.TrackDate.Parse(t.ImportDate) is not null)
                .OrderByDescending(t => Models.TrackDate.Parse(t.ImportDate))
                .FirstOrDefault();

            if (ResolvedFileId is { } id)
                LastSyncedAt = (await _drive.GetMetadataAsync(id, token))?.ModifiedTime;

            return _collection;
        }
        finally
        {
            _inFlight = null;
        }
    }

    private async Task<string> FetchCollectionTextAsync(string token)
    {
        // 1. A file the user explicitly picked always wins.
        var pinned = await GetStoredIdAsync();
        if (!string.IsNullOrEmpty(pinned) && await TryFetchAsync(pinned, token) is { } fromPinned)
            return fromPinned;

        // 2. Otherwise resolve fresh, every time. Discovery is one name query,
        //    and skipping it to reuse a cached id is how the app ended up stuck
        //    on a Traktor 4.4.1 collection after the user upgraded to 4.5.1 --
        //    the old file still resolves, so nothing ever noticed.
        Candidates = await _drive.FindFilesNamedAsync(CollectionFileName, token);

        if (Candidates.Count > 0)
        {
            // Already ordered: live installs before backups, highest Traktor
            // version first, then most recently modified.
            var chosen = Candidates[0];

            if (Candidates.Count > 1)
            {
                // The runner-up goes in the summary, not just the details: when the
                // pick is wrong, "why not that other one" is the whole question, and
                // burying it behind a toggle meant asking the user to go dig.
                var runnerUp = Candidates.Count > 1 ? Candidates[1] : null;
                var unresolved = Candidates.Count(c => c.FolderName is null);

                _errors.Info(
                    $"Found {Candidates.Count} collection.nml files — using {chosen.Describe()}"
                  + (runnerUp is null ? "" : $" · next best: {runnerUp.Describe()}")
                  + (unresolved > 0 ? $" · {unresolved} could not be placed in a folder" : "")
                  + ". Pick a different one below if that is wrong.",
                    string.Join("\n", Candidates.Select((c, i) => $"{(i == 0 ? "->" : "  ")} {c.Describe()}")));
            }

            var content = await _drive.DownloadTextAsync(chosen.Id, token);
            ResolvedFileId = chosen.Id;
            return content;
        }

        // 3. Nothing found by name — fall back to the id this app shipped with.
        if (await TryFetchAsync(LegacyCollectionFileId, token) is { } fromLegacy)
            return fromLegacy;

        throw new DriveRequestException(
            $"No file named {CollectionFileName} found in this Drive account. "
          + "Check you signed in with the account that holds the Traktor collection.");
    }

    private async Task<string?> TryFetchAsync(string fileId, string token)
    {
        try
        {
            var content = await _drive.DownloadTextAsync(fileId, token);
            ResolvedFileId = fileId;
            return content;
        }
        catch (DriveAuthException)
        {
            throw; // an expired token is not something searching can fix
        }
        catch (DriveRequestException)
        {
            return null; // 404 / gone — fall through to discovery
        }
    }

    private static bool LooksLikeNml(string content) =>
        content.TrimStart().StartsWith('<');

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private async Task<string?> GetStoredIdAsync()
    {
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey); }
        catch { return null; }
    }

    private async Task SetStoredIdAsync(string id)
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, id); }
        catch { /* storage unavailable — not worth failing the load over */ }
    }

    /// <summary>Forget the remembered file id, so the next load re-discovers.</summary>
    public async Task ForgetFileIdAsync()
    {
        try { await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey); }
        catch { /* ignored */ }
        Invalidate();
    }

    /// <summary>
    /// Pin a specific collection.nml. The heuristic (newest Traktor version
    /// folder, backups last) is a guess; an install with eleven copies of the
    /// file needs a manual override to be reliable.
    /// </summary>
    public async Task UseFileIdAsync(string fileId)
    {
        await SetStoredIdAsync(fileId);
        ResolvedFileId = fileId;
        Invalidate();
    }

    /// <summary>
    /// Lists every collection.nml so the user can choose. Safe to call after a
    /// successful load, which may not have needed to search.
    /// </summary>
    public async Task<IReadOnlyList<DriveService.NamedFile>> ListCandidatesAsync()
    {
        if (Candidates.Count > 0) return Candidates;
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return [];
        Candidates = await _drive.FindFilesNamedAsync(CollectionFileName, token);
        return Candidates;
    }

    /// <summary>
    /// Folders worth showing: the parser hands back Traktor's $ROOT node as a
    /// Folder alongside its own children, and folders with no playlists are
    /// noise in a sidebar.
    /// </summary>
    public async Task<List<Folder>> GetFoldersAsync()
    {
        var collection = await GetCollectionAsync();
        return collection.Folders
            .Where(f => !string.Equals(f.Name, TraktorRootNodeName, StringComparison.Ordinal))
            .Where(f => f.Playlists.Count > 0)
            .ToList();
    }

    /// <summary>Playlists sitting directly under $ROOT, which have no folder of their own.</summary>
    public async Task<List<Playlist>> GetRootPlaylistsAsync()
    {
        var collection = await GetCollectionAsync();
        return collection.Folders
            .Where(f => string.Equals(f.Name, TraktorRootNodeName, StringComparison.Ordinal))
            .SelectMany(f => f.Playlists)
            .ToList();
    }

    public async Task<Playlist?> GetPlaylistByUuid(string uuid)
    {
        var collection = await GetCollectionAsync();
        return collection.Folders
            .SelectMany(f => f.Playlists)
            .FirstOrDefault(p => p.Uuid == uuid);
    }

    public void Invalidate()
    {
        _collection = null;
        _inFlight = null;
    }
}
