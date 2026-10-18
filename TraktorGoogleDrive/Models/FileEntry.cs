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
        public string? Title => Track?.Title;
        public double? PlaytimeFloat => Track?.PlaytimeFloat;
    }
}
