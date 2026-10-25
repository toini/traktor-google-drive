using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Discover;

/// <summary>
/// Name handling for track discovery (ADR 20261025-54 §3.1, §6.2): splitting
/// "A & B" into artists, the stop list, and the normalised keys that match a
/// store's "obwigszyh - 1616 (Original Mix)" to the collection's "Obwigszyh",
/// "1616 (Original Mix)".
/// </summary>
public static partial class Names
{
    [GeneratedRegex(@"\s*(?:,|&|\bfeat\.?\s|\bft\.?\s|\bfeaturing\s|\bvs\.?\s|\sx\s)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparators();

    [GeneratedRegex(@"[\(\[]\s*(?:feat\.?|ft\.?|featuring)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex FeatInBrackets();

    [GeneratedRegex(@"\s(?:feat\.?|ft\.?|featuring)\s.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatTail();

    [GeneratedRegex(@"[\(\[]\s*original\s+mix\s*[\)\]]|\boriginal\s+mix\b", RegexOptions.IgnoreCase)]
    private static partial Regex OriginalMix();

    private static readonly HashSet<string> StopNames = new(StringComparer.Ordinal)
    {
        "various artists", "various", "va", "v a", "nn", "n n", "unknown", "unknown artist", "distrokid", "",
    };

    /// <summary>Lower case, accents folded, "&amp;" as "and", punctuation dropped, spaces collapsed.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var decomposed = s.Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>"A &amp; B feat. C" → A, B, C. Stop-listed names are dropped.</summary>
    public static IReadOnlyList<string> SplitArtists(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return [];
        return ArtistSeparators().Split(artist)
            .Select(a => a.Trim())
            .Where(a => a.Length > 0 && !IsStopName(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Never a seed: placeholders, distributors, Traktor separator rows, and
    /// store pages pasted into a field (one collection track's artist holds one).
    /// </summary>
    public static bool IsStopName(string? name)
    {
        if (name is null) return true;
        if (name.Contains('\n') || name.Contains("Share / Embed", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Trim().All(c => c is '=' or '-' or '_' or '*' or ' ')) return true;
        return StopNames.Contains(Normalize(name));
    }

    /// <summary>A title for matching: "(Original Mix)" and featured artists dropped, other mix names kept.</summary>
    public static string TitleKey(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = FeatInBrackets().Replace(title, " ");
        t = OriginalMix().Replace(t, " ");
        t = FeatTail().Replace(t, " ");
        return Normalize(t);
    }

    /// <summary>The artists of a credit, normalised, for overlap tests.</summary>
    public static IReadOnlySet<string> ArtistKeys(string? artist) =>
        SplitArtists(artist).Select(Normalize).Where(a => a.Length > 0).ToHashSet();

    /// <summary>A label or artist name as a seed key: "Carnivor Records" → "carnivor records".</summary>
    public static string EntityKey(string? name) => Normalize(name);

    /// <summary>A guess at a Bandcamp subdomain: "Carnivor Records" → "carnivorrecords". Only ever tried on Toni's click.</summary>
    public static string BandcampSlugGuess(string name) => Normalize(name).Replace(" ", "");
}
