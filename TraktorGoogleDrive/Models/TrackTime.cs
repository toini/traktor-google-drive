namespace TraktorGoogleDrive.Models;

public static class TrackTime
{
    /// <summary>
    /// Playback position. Unlike a length, zero is a real value here — the start
    /// of the track — so it must read 0:00 rather than "unknown".
    /// </summary>
    public static string Position(double seconds) =>
        Format(seconds <= 0 ? null : seconds) is "—" ? "0:00" : Format(seconds);

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
