using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Discover;

/// <summary>
/// A release as a store-independent key (ADR 20261025-54 §4.3): a release heard on
/// Beatport must stay heard when Bandcamp or Discogs lists it. Stores spell the same
/// release differently ("DS054 - Terror Protocol EP" vs "Terror Protocol EP",
/// "V.1" vs "Vol. 1"), so the key is the catalogue number when known, else the
/// normalised title, checked against the label and artists when both sides have them.
/// </summary>
public sealed partial record ReleaseMatch(string TitleKey, string? LabelKey, IReadOnlySet<string> Artists, string? Catalog)
{
    // A leading catalogue number: "DS054 - ", "ER031 ", "[CARN012] ".
    [GeneratedRegex(@"^\s*[\[\(]?(?<cat>[A-Za-z]{2,8}\s?-?\d{2,5})[\]\)]?\s*(?:[-–:|]\s*)?(?=\S)")]
    private static partial Regex LeadingCatalog();

    [GeneratedRegex(@"\b(?:volume|vol|v)\s*(\d+)\b")]
    private static partial Regex Volume();

    /// <summary>Titles shorter than this match only with a label or catalogue number to back them.</summary>
    private const int MinTitleOnly = 12;

    public static ReleaseMatch? From(string? title, string? label, string? artist, string? catalog)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(catalog)) return null;
        var t = title ?? "";
        var cat = NormalizeCatalog(catalog);
        var m = LeadingCatalog().Match(t);
        if (m.Success && t.Length > m.Length)
        {
            cat ??= NormalizeCatalog(m.Groups["cat"].Value);
            t = t[m.Length..];
        }
        var key = Volume().Replace(Names.Normalize(t), "v $1");
        var labelKey = Names.Normalize(label);
        return new ReleaseMatch(key, labelKey.Length == 0 ? null : labelKey, Names.ArtistKeys(artist), cat);
    }

    public bool Matches(ReleaseMatch other)
    {
        var labelsAgree = LabelKey is null || other.LabelKey is null || LabelKey == other.LabelKey;
        if (Catalog is not null && other.Catalog is not null)
            return Catalog == other.Catalog && labelsAgree;

        if (TitleKey.Length == 0 || TitleKey != other.TitleKey || !labelsAgree) return false;
        if (Artists.Count > 0 && other.Artists.Count > 0 && !Artists.Overlaps(other.Artists)) return false;
        return TitleKey.Length >= MinTitleOnly || (LabelKey is not null && other.LabelKey is not null);
    }

    private static string? NormalizeCatalog(string? catalog)
    {
        if (string.IsNullOrWhiteSpace(catalog)) return null;
        var c = Regex.Replace(catalog.ToUpperInvariant(), @"[^A-Z0-9]", "");
        // "ER31" and "ER031" are the same number.
        var m = Regex.Match(c, @"^([A-Z]+)0*(\d+)$");
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : c;
    }
}
