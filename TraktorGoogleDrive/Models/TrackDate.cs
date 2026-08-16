using System.Globalization;

namespace TraktorGoogleDrive.Models;

public static class TrackDate
{
    // Traktor writes IMPORT_DATE unpadded, e.g. "2023/3/12" and "2023/10/10".
    // Sorting those as strings puts October before March, so every use has to
    // go through a real date.
    private static readonly string[] Formats = ["yyyy/M/d", "yyyy/MM/dd"];

    public static DateOnly? Parse(string? traktorDate) =>
        DateOnly.TryParseExact(traktorDate, Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d)
            ? d
            : null;

    /// <summary>ISO form — unambiguous and column-aligned.</summary>
    public static string Format(string? traktorDate) =>
        Parse(traktorDate)?.ToString("yyyy-MM-dd") ?? "—";
}
