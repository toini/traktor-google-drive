namespace TraktorGoogleDrive.Services;

/// <summary>
/// The single place that knows how an audio element reaches a Drive file.
/// Today that is the server proxy, because &lt;audio src&gt; cannot set an
/// Authorization header. If the app moves to a Service Worker that attaches the
/// header itself, only this method changes.
/// </summary>
public static class DriveAudio
{
    public static string UrlFor(string fileId, string token) =>
        $"/api/proxy/drive/{Uri.EscapeDataString(fileId)}?token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// The same URL, absolute. A Cast device fetches the media itself, from its own
    /// place on the network, so a relative path means nothing to it.
    /// </summary>
    public static string AbsoluteUrlFor(string baseUri, string fileId, string token) =>
        new Uri(new Uri(baseUri), UrlFor(fileId, token)).ToString();
}
