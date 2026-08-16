using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Services;

public class CollectionService
{
    // The Traktor collection.nml in Drive.
    private const string CollectionFileId = "1yqP8GXUb9qLV8gXRLpvKpyy7DDY7CqAC";

    // Traktor's own root node. It is a container, not a folder the user made,
    // so it should never appear in the sidebar as if it were one.
    private const string TraktorRootNodeName = "$ROOT";

    private readonly DriveService _drive;
    private readonly AuthService _auth;

    private Collection? _collection;
    private Task<Collection>? _inFlight;

    public CollectionService(DriveService drive, AuthService auth)
    {
        _drive = drive;
        _auth = auth;
    }

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
                ?? throw new DriveAuthException("No access token");

            var content = await _drive.DownloadTextAsync(CollectionFileId, token);
            var parser = new TraktorNmlParser.NmlParser();
            _collection = parser.Load(content);
            return _collection;
        }
        finally
        {
            _inFlight = null;
        }
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
