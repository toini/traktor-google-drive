namespace TraktorGoogleDrive.Models
{
    public class FileEntry
    {
        public string DriveFileId { get; set; } = string.Empty;
        public string DriveFileName { get; set; } = string.Empty;
        public string DriveFileMimeType { get; set; } = string.Empty;
        public string PeaksUrl { get; set; } = string.Empty;
        public TraktorNmlParser.Models.Track? Track { get; set; }
        /// <summary>1-based position in the Traktor playlist it was loaded from; 0 outside a playlist.</summary>
        public int PlaylistIndex { get; set; }
        /// <summary>Traktor's star rating, INFO RANKING 0-255 (51 per star); null when unrated. Set by the playlist page.</summary>
        public int? Ranking { get; set; }
        /// <summary>The release (album) title, ALBUM TITLE; null when absent. Set by the playlist page.</summary>
        public string? Release { get; set; }
        /// <summary>What the playlist ENTRY is; anything but Track never gets a Drive file.</summary>
        public EntryKind Kind { get; set; } = EntryKind.Track;
        /// <summary>Row title when the track has none: the key's file name, or the stream's address.</summary>
        public string EntryTitle { get; set; } = string.Empty;
        /// <summary>The playlist ENTRY's PRIMARYKEY KEY, as Traktor wrote it.</summary>
        public string EntryKey { get; set; } = string.Empty;
        /// <summary>Has a Drive file, so it gets a play button and a place in the queue.</summary>
        public bool IsPlayable => Kind == EntryKind.Track && !string.IsNullOrEmpty(DriveFileId);
        public string? Title => Track?.Title;
        public double? PlaytimeFloat => Track?.PlaytimeFloat;
    }
}
