using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Models;

/// <summary>
/// Reduces a recording filename or a playlist name to the "set" it belongs to,
/// so a recording can be matched to the playlists that were built for it.
/// </summary>
/// <remarks>
/// Two naming conventions coexist in the collection and need opposite handling:
/// a gig is <c>2025-04-19 Golgatan farssi</c> — the date is the identity, keep
/// it — while a numbered set is <c>Z15.2 2023-04-15</c>, where the trailing
/// date is just when it was played and the code is the identity.
/// Measured against the real collection this resolves ~81% of recordings; the
/// rest are genuinely ambiguous or have no playlist at all, and are left for
/// the user to pick.
/// </remarks>
public static partial class SetName
{
    [GeneratedRegex(@"\.(wav|mp3|m4a|flac|aiff?)$", RegexOptions.IgnoreCase)]
    private static partial Regex Extension();

    // Suffixes that mark a variant of the same set rather than a different set.
    [GeneratedRegex(
        @"[\s._-]*(rec\d*|raw|orig|mastered|edit\d*|poista|prep\w*|treeni|level\d|karsittu"
      + @"|reordered|lyhennetty|preparation[\w-]*|bak\s*\d*|all|gig|bileet|v\d+|\(\d+\)"
      + @"|liian\s+lyhyt|gain\+limiter.*|no\s+levels|\d+h\d+m\d+)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VariantSuffix();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    private static partial Regex LeadingDate();

    [GeneratedRegex(@"[\s._-]+\d{4}-\d{1,2}-\d{1,2}\s*$")]
    private static partial Regex TrailingDate();

    [GeneratedRegex(@"[\s._-]+\d{4}\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"[\s._-]*-\d+\s*$")]
    private static partial Regex TrailingIndex();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    public static string Base(string name)
    {
        var s = Extension().Replace(name, "");
        // A leading date IS the set's identity, so nothing trailing may be read
        // as "the date" and stripped.
        var dateLed = LeadingDate().IsMatch(s);

        string previous;
        do
        {
            previous = s;
            s = VariantSuffix().Replace(s, "");
            if (!dateLed)
            {
                s = TrailingDate().Replace(s, "");
                s = TrailingYear().Replace(s, "");
                s = TrailingIndex().Replace(s, "");
            }
        } while (previous != s);

        return s.Trim(' ', '-', '.', '_');
    }

    public static string Key(string name) => NonAlphanumeric().Replace(Base(name).ToLowerInvariant(), "");

    /// <summary>A ".rec" playlist is the order actually played, so it ranks first.</summary>
    public static bool IsRecorded(string playlistName) =>
        Regex.IsMatch(playlistName, @"\.rec\d*$", RegexOptions.IgnoreCase);
}
