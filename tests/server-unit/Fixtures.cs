namespace TraktorGoogleDrive.Tests;

internal static class Fixtures
{
    public static string Bandcamp(string name) => Read("bandcamp", name);

    /// <summary>Discogs API answers recorded in the crate's live pass (2026-11-01), trimmed to what the parser reads.</summary>
    public static string Discogs(string name) => Read("discogs", name);

    public static string SoundCloud(string name) => Read("soundcloud", name);

    private static string Read(string source, string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "discover", source, name));
}
