namespace TraktorGoogleDrive.Models;

/// <summary>
/// Where a discovery preview's audio comes from (ADR 20261025-54 §2.3).
/// <paramref name="StreamUrl"/> is our own endpoint, which answers with a redirect
/// to the store's CDN, so the bytes never pass through Cloud Run.
/// </summary>
public sealed record PlayableSource(
    string Source,
    string StreamUrl,
    string? PageUrl,
    string? Label,
    string? ReleaseTitle);

/// <summary>
/// The player's item model (ADR 20261025-54 §2.3). The player, prefetch,
/// waveform, Cast and remote code are all keyed by <see cref="FileEntry"/>, so a
/// discovery preview is a FileEntry with a <see cref="FileEntry.External"/>
/// source, and every Drive-only path asks its capability flags
/// (<see cref="FileEntry.CanCache"/>, <see cref="FileEntry.CanWaveform"/>,
/// <see cref="FileEntry.CanCast"/>) instead of assuming a Drive file.
/// </summary>
public static class PlayableItem
{
    public const string DiscoverPrefix = "discover:";

    public static bool IsDiscoverId(string? id) => id?.StartsWith(DiscoverPrefix, StringComparison.Ordinal) == true;

    public static FileEntry Discover(string trackKey, string title, string artist, double duration,
        string streamUrl, string? pageUrl, string? label, string? releaseTitle) => new()
    {
        DriveFileId = DiscoverPrefix + trackKey,
        DriveFileName = title,
        EntryTitle = $"{artist} – {title}",
        Track = new TraktorNmlParser.Models.Track
        {
            Title = $"{artist} – {title}",
            Artist = artist,
            Label = label,
            PlaytimeFloat = duration > 0 ? duration : null,
        },
        Release = releaseTitle,
        External = new PlayableSource(trackKey.Split(':')[0], streamUrl, pageUrl, label, releaseTitle),
    };
}
