namespace TraktorGoogleDrive.Models
{
    public class FileEntry
    {
        public string Id { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public string PeaksUrl { get; set; } = string.Empty;
        public required TraktorNmlParser.Models.Track Track { get; set; }

        public string Name => Track.Title;
        public double? PlaytimeFloat => Track.PlaytimeFloat;
    }
}
