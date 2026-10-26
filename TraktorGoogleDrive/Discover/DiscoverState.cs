using System.Text.Json;
using System.Text.Json.Serialization;

namespace TraktorGoogleDrive.Discover;

/// <summary>
/// One line of a device's event log in the Drive appDataFolder (ADR 20261025-54
/// §8, S-A). Append-only, and each device writes only its own file, so the tablet
/// and the laptop never overwrite each other. State is the fold of every device's
/// events, the later one winning field by field.
/// </summary>
public sealed record DiscoverEvent
{
    /// <summary>seed-add, seed-remove, seed-url, watermark, track, release-heard, stop-name.</summary>
    [JsonPropertyName("t")] public required string Type { get; init; }
    [JsonPropertyName("at")] public required DateTimeOffset At { get; init; }
    [JsonPropertyName("dev")] public string Device { get; init; } = "";
    [JsonPropertyName("n")] public long Seq { get; init; }

    // Seeds
    [JsonPropertyName("seed")] public string? SeedKey { get; init; }
    [JsonPropertyName("kind")] public SeedKind? Kind { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("genre")] public string? Genre { get; init; }
    [JsonPropertyName("origin")] public SeedOrigin? Origin { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("month")] public string? Month { get; init; }

    // Tracks
    [JsonPropertyName("track")] public string? TrackKey { get; init; }
    [JsonPropertyName("release")] public string? ReleaseUrl { get; init; }
    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("rtitle")] public string? ReleaseTitle { get; init; }
    [JsonPropertyName("rdate")] public DateOnly? ReleaseDate { get; init; }
    /// <summary>Catalogue number of a heard release, for matching it across stores.</summary>
    [JsonPropertyName("cat")] public string? Catalog { get; init; }
    [JsonPropertyName("pass1")] public Pass1? Pass1 { get; init; }
    [JsonPropertyName("rating")] public int? Rating { get; init; }
    [JsonPropertyName("decision")] public Decision? Decision { get; init; }
    [JsonPropertyName("note")] public string? Note { get; init; }
    [JsonPropertyName("bought")] public bool? Bought { get; init; }
    /// <summary>Clears pass1, rating and decision: the undo of a mis-tap. "Heard" stays (ADR §4.3).</summary>
    [JsonPropertyName("clear")] public bool? Clear { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<Pass1>))]
public enum Pass1 { Skip, Maybe, Like }

[JsonConverter(typeof(JsonStringEnumConverter<Decision>))]
public enum Decision { Buy, Skip }

public sealed class Seed
{
    public required string Key { get; init; }
    public required SeedKind Kind { get; init; }
    public required string Name { get; set; }
    public required string Genre { get; init; }
    public SeedOrigin Origin { get; set; }
    public string? Reason { get; set; }
    public string? BandcampUrl { get; set; }
    /// <summary>"Heard everything through this month", YYYY-MM. Only ever moves forward.</summary>
    public string? Watermark { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

public sealed class TrackState
{
    public required string Key { get; init; }
    public string? ReleaseUrl { get; set; }
    public string? TrackUrl { get; set; }
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public string? Label { get; set; }
    public string? ReleaseTitle { get; set; }
    public DateOnly? ReleaseDate { get; set; }
    public string? Genre { get; set; }
    /// <summary>Heard is permanent and separate from the verdict (ADR §4.3).</summary>
    public bool Heard { get; set; }
    public Pass1? Pass1 { get; set; }
    public int? Rating { get; set; }
    public Decision? Decision { get; set; }
    public string? Note { get; set; }
    public bool Bought { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>The fold. Rebuilt from all logs on load, then updated event by event.</summary>
public sealed class DiscoverState
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public Dictionary<string, Seed> Seeds { get; } = new();
    public Dictionary<string, TrackState> Tracks { get; } = new();
    public HashSet<string> HeardReleases { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Names Toni removed from proposals ("never suggest").</summary>
    public HashSet<string> StopNames { get; } = new(StringComparer.Ordinal);
    /// <summary>Heard releases as store-independent keys, so the same release on another store is heard too.</summary>
    public List<ReleaseMatch> HeardReleaseMatches { get; } = [];

    /// <summary>A release is heard by its URL, or by matching a heard release from another store.</summary>
    public bool IsReleaseHeard(string url, string? title, string? label, string? artist) =>
        HeardReleases.Contains(url)
        || (HeardReleaseMatches.Count > 0 && ReleaseMatch.From(title, label, artist, null) is { } m
            && HeardReleaseMatches.Any(h => h.Matches(m)));

    /// <summary>Versions of the starter list already applied to this log (never applied twice).</summary>
    public HashSet<string> StarterVersions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Canonical track URLs of every heard track. A track known only by its URL
    /// (the starter list, later the OneNote import) has a <c>url:</c> key until its
    /// page is read, and must still never be queued (ADR §4.3).
    /// </summary>
    public IReadOnlySet<string> HeardTrackUrls =>
        Tracks.Values.Where(t => t.TrackUrl is not null).Select(t => TrackUrlKey(t.TrackUrl!)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Key of a track known only by URL: "url:https://x.bandcamp.com/track/slug".</summary>
    public static string UrlTrackKey(string trackUrl) => "url:" + TrackUrlKey(trackUrl);

    public static string TrackUrlKey(string url) =>
        url.Trim().ToLowerInvariant().Replace("http://", "https://").Split('?', '#')[0].TrimEnd('/');

    public static DiscoverState Fold(IEnumerable<DiscoverEvent> events)
    {
        var state = new DiscoverState();
        foreach (var e in events.OrderBy(e => e.At).ThenBy(e => e.Device, StringComparer.Ordinal).ThenBy(e => e.Seq))
            state.Apply(e);
        return state;
    }

    public void Apply(DiscoverEvent e)
    {
        switch (e.Type)
        {
            case "seed-add" when e.SeedKey is not null && e.Kind is { } kind && e.Name is not null && e.Genre is not null:
                if (!Seeds.TryGetValue(e.SeedKey, out var existing))
                {
                    Seeds[e.SeedKey] = new Seed
                    {
                        Key = e.SeedKey, Kind = kind, Name = e.Name, Genre = e.Genre,
                        Origin = e.Origin ?? SeedOrigin.Manual, Reason = e.Reason, BandcampUrl = e.Url, AddedAt = e.At,
                    };
                }
                else
                {
                    // Re-adding keeps the stronger origin ("eti lisää" beats a top list).
                    if (e.Origin is { } o && o < existing.Origin) { existing.Origin = o; existing.Reason = e.Reason ?? existing.Reason; }
                    existing.BandcampUrl ??= e.Url;
                }
                break;

            case "seed-remove" when e.SeedKey is not null:
                Seeds.Remove(e.SeedKey);
                break;

            case "seed-url" when e.SeedKey is not null && Seeds.TryGetValue(e.SeedKey, out var s):
                s.BandcampUrl = string.IsNullOrWhiteSpace(e.Url) ? null : e.Url;
                break;

            case "watermark" when e.SeedKey is not null && Seeds.TryGetValue(e.SeedKey, out var w):
                // Imports and "heard through" only raise it; Toni editing the field
                // by hand (Clear) may also lower or remove it.
                if (e.Clear == true) w.Watermark = IsWatermark(e.Month) ? e.Month : null;
                else if (LastDay(e.Month) is { } raised && (LastDay(w.Watermark) is not { } current || raised > current))
                    w.Watermark = e.Month;
                break;

            case "release-heard" when e.ReleaseUrl is not null:
                HeardReleases.Add(e.ReleaseUrl);
                if (ReleaseMatch.From(e.ReleaseTitle, e.Label, e.Artist, e.Catalog) is { } match) HeardReleaseMatches.Add(match);
                break;

            case "starter" when e.Name is not null:
                StarterVersions.Add(e.Name);
                break;

            case "stop-name" when e.Name is not null:
                StopNames.Add(Names.Normalize(e.Name));
                break;

            case "track" when e.TrackKey is not null:
                if (!Tracks.TryGetValue(e.TrackKey, out var t)) Tracks[e.TrackKey] = t = new TrackState { Key = e.TrackKey };
                t.ReleaseUrl = e.ReleaseUrl ?? t.ReleaseUrl;
                t.TrackUrl = e.Url ?? t.TrackUrl;
                t.Artist = e.Artist ?? t.Artist;
                t.Title = e.Title ?? t.Title;
                t.Label = e.Label ?? t.Label;
                t.ReleaseTitle = e.ReleaseTitle ?? t.ReleaseTitle;
                t.ReleaseDate = e.ReleaseDate ?? t.ReleaseDate;
                t.Genre = e.Genre ?? t.Genre;
                t.Heard = true;
                if (e.Clear == true) { t.Pass1 = null; t.Rating = null; t.Decision = null; }
                if (e.Pass1 is { } p) t.Pass1 = p;
                if (e.Rating is { } r) t.Rating = r is >= 1 and <= 5 ? r : null;
                if (e.Decision is { } d) t.Decision = d;
                if (e.Note is not null) t.Note = e.Note.Length == 0 ? null : e.Note;
                if (e.Bought is { } b) t.Bought = b;
                t.DecidedAt = e.At;
                break;
        }
    }

    public static bool IsMonth(string? month) =>
        month is { Length: 7 } && DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", out _);

    /// <summary>A watermark is a month ("2023-06", OneNote's precision) or a day ("2026-10-25").</summary>
    public static bool IsWatermark(string? value) => LastDay(value) is not null;

    /// <summary>The last day a watermark covers: the day itself, or a month's last day.</summary>
    public static DateOnly? LastDay(string? watermark)
    {
        if (watermark is null) return null;
        if (watermark.Length == 10 && DateOnly.TryParseExact(watermark, "yyyy-MM-dd", out var day)) return day;
        if (IsMonth(watermark))
        {
            var first = DateOnly.ParseExact(watermark + "-01", "yyyy-MM-dd");
            return first.AddMonths(1).AddDays(-1);
        }
        return null;
    }

    /// <summary>True when a release dated <paramref name="date"/> is at or before the seed's watermark.</summary>
    public static bool BeforeWatermark(DateOnly? date, string? watermark) =>
        date is { } d && LastDay(watermark) is { } last && d <= last;
}
