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
        /// <summary>
        /// Set for a discovery preview (ADR 20261025-54 §2): a store's stream rather
        /// than a Drive file. <see cref="DriveFileId"/> then holds a synthetic id
        /// ("discover:bandcamp:&lt;trackId&gt;") so the queue and the remote snapshot
        /// work unchanged. See <see cref="PlayableItem"/>.
        /// </summary>
        public PlayableSource? External { get; set; }
        /// <summary>Downloadable whole for offline play (prefetch.js). A store stream is not: no CORS.</summary>
        public bool CanCache => External is null;
        /// <summary>Has bytes this browser can read for a waveform. A store stream does not.</summary>
        public bool CanWaveform => External is null;
        /// <summary>A Cast device can be handed it. Not a store stream: the TV cannot use our cookie.</summary>
        public bool CanCast => External is null;
        public string? Title => Track?.Title;
        public double? PlaytimeFloat => Track?.PlaytimeFloat;
    }
}
