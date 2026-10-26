namespace TraktorGoogleDrive.Discover;

/// <summary>Toni's four genres, by the Traktor folder that holds their playlists (ADR 20261025-54).</summary>
public static class Genres
{
    public static readonly IReadOnlyList<string> All = ["Z", "H", "P", "C"];

    public static string Name(string genre) => genre switch
    {
        "Z" => "Dark prog",
        "H" => "Hard techno",
        "P" => "Psytrance",
        "C" => "Dark tek chillout",
        _ => genre,
    };

    public static bool IsGenre(string? folder) => folder is not null && All.Contains(folder);
}
