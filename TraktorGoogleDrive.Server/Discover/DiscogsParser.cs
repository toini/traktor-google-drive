using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>One row of a Discogs database search, as the ranking sees it.</summary>
public sealed record DiscogsSearchResult(
    long Id, string Artist, string Title, string? Year, IReadOnlyList<string> Labels, string? Catno,
    IReadOnlyList<string> Formats, string? Uri);

/// <summary>A Discogs release, read for its tracklist, labels, date and links.</summary>
public sealed record DiscogsRelease(
    long Id, string Title, IReadOnlyList<DiscogsCredit> Artists, IReadOnlyList<DiscogsCredit> Labels,
    string? Released, string? Year, string? Uri, IReadOnlyList<DiscogsTrack> Tracklist, IReadOnlyList<string> NoteUrls);

public sealed record DiscogsCredit(long Id, string Name);

public sealed record DiscogsTrack(string? Position, string Title, IReadOnlyList<string> Artists);

/// <summary>A label's or artist's profile: only its links matter here.</summary>
public sealed record DiscogsProfile(long Id, string Name, IReadOnlyList<string> Urls);

/// <summary>
/// Pure parsing of the Discogs API's JSON (ADR 20261025-54 §1.4, increment 3), so
/// the tests run on recorded responses and never call Discogs.
/// </summary>
public static partial class DiscogsParser
{
    [GeneratedRegex(@"https?://[a-z0-9-]+\.bandcamp\.com[^\s""'<>\]\)]*", RegexOptions.IgnoreCase)]
    private static partial Regex BandcampLink();

    public static IReadOnlyList<DiscogsSearchResult> ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
        var list = new List<DiscogsSearchResult>();
        foreach (var r in results.EnumerateArray())
        {
            if (Str(r, "type") is { } type && type != "release") continue;
            if (!r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
            // "Artist - Release": the search's only artist field.
            var full = Str(r, "title") ?? "";
            var dash = full.IndexOf(" - ", StringComparison.Ordinal);
            var artist = dash > 0 ? full[..dash] : "";
            var title = dash > 0 ? full[(dash + 3)..] : full;
            list.Add(new DiscogsSearchResult(id, artist.Trim(), title.Trim(), Str(r, "year"), Strings(r, "label"),
                Str(r, "catno"), Strings(r, "format"), Str(r, "uri")));
        }
        return list;
    }

    public static DiscogsRelease? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) return null;

        var tracks = new List<DiscogsTrack>();
        if (r.TryGetProperty("tracklist", out var tl) && tl.ValueKind == JsonValueKind.Array)
            foreach (var t in tl.EnumerateArray())
            {
                if (Str(t, "type_") is { } kind && kind != "track") continue;
                tracks.Add(new DiscogsTrack(Str(t, "position"), Str(t, "title") ?? "",
                    Credits(t, "artists").Select(c => c.Name).ToList()));
            }

        var notes = Str(r, "notes") ?? "";
        return new DiscogsRelease(id, Str(r, "title") ?? "", Credits(r, "artists"), Credits(r, "labels"),
            Str(r, "released"), r.TryGetProperty("year", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetInt32().ToString(CultureInfo.InvariantCulture) : Str(r, "year"),
            Str(r, "uri"), tracks, BandcampLink().Matches(notes).Select(m => m.Value).Distinct().ToList());
    }

    public static DiscogsProfile? ParseProfile(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) return null;
        return new DiscogsProfile(id, Str(r, "name") ?? "", Strings(r, "urls"));
    }

    /// <summary>The Bandcamp label or artist pages among a profile's links, as canonical /music pages.</summary>
    public static IReadOnlyList<string> BandcampPages(IEnumerable<string> urls) =>
        urls.Select(u => BandcampUrls.ReleasePage(u)?.ToString() ?? BandcampUrls.MusicPage(u)?.ToString())
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static IReadOnlyList<DiscogsCredit> Credits(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        var list = new List<DiscogsCredit>();
        foreach (var c in v.EnumerateArray())
        {
            var id = c.TryGetProperty("id", out var i) && i.TryGetInt64(out var n) ? n : 0;
            if (Str(c, "name") is { Length: > 0 } nm) list.Add(new DiscogsCredit(id, nm));
        }
        return list;
    }
}

/// <summary>
/// Name keys for matching a set's tracklist against Discogs: lower case, accents
/// folded, Discogs' "(2)" disambiguation and "*" name-variation marks dropped.
/// The client has its own (Discover/Names.cs); the server cannot reference it.
/// </summary>
public static partial class DiscogsNames
{
    [GeneratedRegex(@"\s\(\d+\)$")]
    private static partial Regex Disambiguation();

    [GeneratedRegex(@"\s*(?:,|&|\band\b|\bfeat\.?\s|\bft\.?\s|\bfeaturing\s|\bvs\.?\s|\sx\s)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separators();

    [GeneratedRegex(@"[\(\[]\s*original\s+mix\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoise();

    [GeneratedRegex(@"\(\s*truncated\s*\)|\btruncated\)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TruncatedMark();

    public static bool IsTruncated(string? title) =>
        title is not null && (title.Contains('…') || title.Contains("truncated", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A title as transcribed from a screenshot, made searchable: the part before
    /// "…", without "(truncated)" or a bracket the cut left open
    /// ("The Cosmos Welcomes You (Psilocybin Intellig… truncated)" → "The Cosmos Welcomes You").
    /// </summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title;
        var cut = t.IndexOf('…');
        if (cut >= 0) t = t[..cut];
        t = TruncatedMark().Replace(t, " ").Trim();
        var open = t.LastIndexOf('(');
        if (open >= 0 && t.IndexOf(')', open) < 0 && IsTruncated(title)) t = t[..open].Trim();
        return t;
    }

    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"Tengri (3) &amp; Jahbo*" → {"tengri", "jahbo"}.</summary>
    public static IReadOnlySet<string> ArtistKeys(string? credit) =>
        Separators().Split(credit ?? "")
            .Select(a => Normalize(Disambiguation().Replace(a.Trim().TrimEnd('*'), "")))
            .Where(a => a.Length > 0)
            .ToHashSet();

    /// <summary>The same, keeping Discogs' "(2)": two artists who share a name are two identities.</summary>
    public static IReadOnlySet<string> Identities(string? credit) =>
        Separators().Split(credit ?? "")
            .Select(a => a.Trim().TrimEnd('*').ToLowerInvariant())
            .Where(a => a.Length > 0)
            .ToHashSet();

    public static bool IsVarious(string? credit) => Normalize(credit) is "various" or "various artists" or "va";

    /// <summary>A track title for matching: "(Original Mix)" and the transcription's "(truncated)" dropped.</summary>
    /// <summary>What to send as Discogs' <c>track</c> parameter: the clean title, punctuation kept.</summary>
    public static string SearchTitle(string? title) =>
        string.Join(' ', TitleNoise().Replace(CleanTitle(title), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string TitleKey(string? title) => Normalize(TitleNoise().Replace(CleanTitle(title), " "));

    /// <summary>Equal, or (for a title cut off in the screenshot) one starting with the other.</summary>
    public static bool TitleMatches(string? wanted, string? candidate, bool truncated)
    {
        var a = TitleKey(wanted);
        var b = TitleKey(candidate);
        if (a.Length == 0 || b.Length == 0) return false;
        return a == b || (truncated && a.Length >= 3 && b.StartsWith(a, StringComparison.Ordinal));
    }
}
