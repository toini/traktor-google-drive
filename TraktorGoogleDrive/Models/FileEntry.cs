namespace TraktorGoogleDrive.Models
{
    public class FileEntry
    {
        public string DriveFileId { get; set; } = string.Empty;
        public string DriveFileName { get; set; } = string.Empty;
        public string DriveFileMimeType { get; set; } = string.Empty;
        public string PeaksUrl { get; set; } = string.Empty;
        public TraktorNmlParser.Models.Track? Track { get; set; }
        public string? Title => Track?.Title;
        public double? PlaytimeFloat => Track?.PlaytimeFloat;
    }
}
