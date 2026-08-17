namespace TraktorGoogleDrive.Services;

/// <summary>
/// The single place that knows how an audio element reaches a Drive file.
/// Today that is the server proxy, because &lt;audio src&gt; cannot set an
/// Authorization header. If the app moves to a Service Worker that attaches the
/// header itself, only this method changes.
/// </summary>
public static class DriveAudio
{
    /// <summary>
    /// Uncompressed audio can have its waveform sampled with a handful of range
    /// reads; a compressed file would have to be fully decoded, so it gets a
    /// plain progress bar instead.
    /// </summary>
    public static bool IsUncompressed(Models.FileEntry file)
    {
        var name = file.DriveFileName is { Length: > 0 } n ? n : file.Track?.Path ?? "";
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".aif", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase);
    }

    public static string UrlFor(string fileId, string token) =>
        $"/api/proxy/drive/{Uri.EscapeDataString(fileId)}?token={Uri.EscapeDataString(token)}";
}
