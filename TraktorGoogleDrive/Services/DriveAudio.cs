namespace TraktorGoogleDrive.Services;

/// <summary>
/// The single place that knows how an audio element reaches a Drive file.
/// Today that is the server proxy, because &lt;audio src&gt; cannot set an
/// Authorization header; the proxy takes the token from a cookie instead (or,
/// for a Cast device, from the query string).
/// </summary>
public static class DriveAudio
{
    /// <summary>
    /// Uncompressed audio can have its waveform sampled with a handful of range
    /// reads; a compressed file has to be downloaded and decoded instead.
    /// </summary>
    public static bool IsUncompressed(Models.FileEntry file)
    {
        var name = file.DriveFileName is { Length: > 0 } n ? n : file.Track?.Path ?? "";
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".aif", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Largest compressed file decoded in the browser for a waveform. A 7-minute
    /// MP3 is ~16 MB; past this it is a whole recorded set, whose download and
    /// decode cost more than the picture is worth (ADR 20261018-47 §1).
    /// </summary>
    public const long CompressedDecodeCapBytes = 150L * 1024 * 1024;

    /// <summary>
    /// File size from the NML: FILESIZE is in KB. Older entries lack it, so fall
    /// back to bitrate × length, and to "unknown" when neither is there.
    /// </summary>
    public static long? EstimatedBytes(Models.FileEntry file)
    {
        var track = file.Track;
        if (track?.FileSize is > 0) return track.FileSize.Value * 1024L;
        if (track?.Bitrate is > 0 && track.PlaytimeFloat is > 0)
            return (long)(track.Bitrate.Value / 8.0 * track.PlaytimeFloat.Value);
        return null;
    }

    /// <summary>Whether a compressed file is small enough to decode for its waveform.</summary>
    public static bool CanDecodeInBrowser(Models.FileEntry file) =>
        // Unknown size: trust it only if it is not set-length.
        EstimatedBytes(file) is { } bytes
            ? bytes <= CompressedDecodeCapBytes
            : (file.Track?.PlaytimeFloat ?? 0) < 30 * 60;

    /// <summary>
    /// Drive's own media URL. Only for fetch(), which can send the token as a
    /// header: it bypasses the Cloud Run proxy and so costs no egress there.
    /// </summary>
    public static string DirectUrlFor(string fileId) =>
        $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media";

    /// <summary>
    /// The proxy URL for this browser's own audio element. No token in it: auth.js
    /// keeps the current token in a cookie scoped to the proxy path, so each range
    /// slice goes out with a fresh one and no URL — nor Cloud Run's request log —
    /// holds a bearer token.
    /// </summary>
    public static string UrlFor(string fileId) =>
        $"/api/proxy/drive/{Uri.EscapeDataString(fileId)}";

    /// <summary>
    /// The URL a Cast device fetches, absolute because it fetches from its own place
    /// on the network. The device has no cookie of ours, so the token still rides in
    /// the query string here, and the sender re-issues the load when it renews.
    /// </summary>
    public static string AbsoluteUrlFor(string baseUri, string fileId, string token) =>
        new Uri(new Uri(baseUri), $"{UrlFor(fileId)}?token={Uri.EscapeDataString(token)}").ToString();
}
