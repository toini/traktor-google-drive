namespace TraktorGoogleDrive.Models;

public static class TrackTime
{
    /// <summary>Formats a track length as m:ss (or h:mm:ss past an hour).</summary>
    public static string Format(double? seconds)
    {
        if (seconds is null or <= 0) return "—";
        var t = TimeSpan.FromSeconds(seconds.Value);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
