namespace TraktorGoogleDrive.Tests;

internal static class Fixtures
{
    public static string Bandcamp(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "discover", "bandcamp", name));
}
