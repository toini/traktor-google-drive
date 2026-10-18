using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Models;

/// <summary>What a Traktor playlist ENTRY turned out to be.</summary>
public enum EntryKind
{
    /// <summary>A collection track. Playable when Drive has the file.</summary>
    Track,

    /// <summary>A "==== Next part" placeholder, used to divide a set. Never played.</summary>
    Separator,

    /// <summary>A Beatport/Beatsource streaming track: no file anywhere to play.</summary>
    Streaming,

    /// <summary>The key matches nothing in this collection.nml.</summary>
    NotInCollection,
}

/// <summary>One ENTRY under a playlist's PLAYLIST node, in Traktor's order.</summary>
/// <param name="Position">1-based, exactly Traktor's own numbering.</param>
/// <param name="Key">PRIMARYKEY KEY as Traktor wrote it.</param>
/// <param name="Title">What to call the row when there is no track title.</param>
public sealed record PlaylistEntry(int Position, string Key, EntryKind Kind, Track? Track, string Title);

/// <summary>
/// Builds every playlist's entries straight from the NML, one per ENTRY.
/// </summary>
/// <remarks>
/// TraktorNmlParser 0.0.11's Playlist.Tracks keeps only the entries whose key
/// it finds in its own track map, and drops the rest without a trace. Its map
/// leaves out every path containing "silent" (which is exactly how Traktor users
/// make separator tracks: a few seconds of silence titled "==== Part 2"), every
/// track without a file (streaming), and it strips only the "Macintosh HD"
/// volume, so a track on any other volume never matches. On a real Traktor 4
/// collection all 699 of the 9282 playlist entries it dropped were separators.
/// This reads the same XDocument and keeps all of them.
/// </remarks>
public static class PlaylistEntries
{
    // Separator text: a run of 3+ of these, alone or leading the title
    // ("====", "==== Nyt alkaa meno", "--- warmup ---").
    private static readonly Regex SeparatorOnly = new(@"^[\s=\-_#*]*[=\-_#*]{3,}[\s=\-_#*]*$");
    private static readonly Regex SeparatorLead = new(@"^\s*[=\-_#*]{3,}(\s|$)");

    public static bool IsSeparatorText(string? text) =>
        !string.IsNullOrWhiteSpace(text) && (SeparatorOnly.IsMatch(text) || SeparatorLead.IsMatch(text));

    /// <summary>The words of a separator, without its rule characters: "==== Part 2" is "Part 2".</summary>
    public static string SeparatorLabel(string? title) =>
        (title ?? "").Trim().Trim('=', '-', '_', '#', '*', ' ', '\t');

    /// <summary>Every playlist's entries, keyed by PLAYLIST UUID.</summary>
    /// <param name="parsedTracks">The parser's tracks, reused so a row and the rest of the app share one Track.</param>
    public static Dictionary<string, List<PlaylistEntry>> Read(XDocument doc, IEnumerable<Track> parsedTracks)
    {
        var parsedByPath = new Dictionary<string, Track>();
        foreach (var t in parsedTracks) parsedByPath.TryAdd(t.Path, t);

        var exact = new Dictionary<string, Track>(StringComparer.Ordinal);
        var loose = new Dictionary<string, Track>(StringComparer.Ordinal);

        foreach (var entry in doc.Root?.Element("COLLECTION")?.Elements("ENTRY") ?? [])
        {
            var location = entry.Element("LOCATION");
            var dir = (string?)location?.Attribute("DIR") ?? "";
            var file = (string?)location?.Attribute("FILE") ?? "";
            var volume = (string?)location?.Attribute("VOLUME") ?? "";
            var web = (string?)location?.Attribute("WEBADDRESS");

            Track track;
            try
            {
                var fromXml = Track.FromXml(entry);
                track = parsedByPath.TryGetValue(fromXml.Path, out var parsed) ? parsed : fromXml;
            }
            catch (Exception)
            {
                continue; // an entry the parser cannot read (e.g. a cue without HOTCUE)
            }

            if (file.Length > 0)
            {
                // Traktor's PRIMARYKEY is VOLUME + DIR + FILE, verbatim.
                exact.TryAdd(volume + dir + file, track);
                loose.TryAdd(LooseKey(dir + file), track);
            }
            if (!string.IsNullOrEmpty(web)) exact.TryAdd(web, track);
        }

        var result = new Dictionary<string, List<PlaylistEntry>>(StringComparer.Ordinal);

        foreach (var list in doc.Descendants("PLAYLIST"))
        {
            var uuid = (string?)list.Attribute("UUID");
            if (string.IsNullOrEmpty(uuid)) continue;

            var entries = new List<PlaylistEntry>();
            var position = 0;
            foreach (var e in list.Elements("ENTRY"))
            {
                position++;
                var primary = e.Element("PRIMARYKEY");
                var key = (string?)primary?.Attribute("KEY") ?? "";
                var type = (string?)primary?.Attribute("TYPE") ?? "";

                var track = key.Length == 0 ? null
                          : exact.GetValueOrDefault(key) ?? loose.GetValueOrDefault(LooseKey(VolumeLess(key)));

                entries.Add(Classify(position, key, type, track));
            }

            result.TryAdd(uuid, entries);
        }

        return result;
    }

    private static PlaylistEntry Classify(int position, string key, string type, Track? track)
    {
        var fileStem = FileStem(key);
        var title = track?.Title is { Length: > 0 } t ? t : fileStem;

        if (IsSeparatorText(track?.Title) || IsSeparatorText(track?.Artist) || IsSeparatorText(fileStem))
            return new PlaylistEntry(position, key, EntryKind.Separator, track, title);

        var streaming = IsStreamingKey(key) || !type.Equals("TRACK", StringComparison.OrdinalIgnoreCase)
                     || (track is not null && string.IsNullOrEmpty(track.Path));
        if (streaming)
            return new PlaylistEntry(position, key, EntryKind.Streaming, track, track?.Title ?? key);

        return track is null
            ? new PlaylistEntry(position, key, EntryKind.NotInCollection, null, title)
            : new PlaylistEntry(position, key, EntryKind.Track, track, title);
    }

    private static bool IsStreamingKey(string key) => key.Contains("://", StringComparison.Ordinal);

    // "Macintosh HD/:Music/:x.mp3" -> "/:Music/:x.mp3". The volume name is the
    // part that differs between machines (and is "C:" on Windows).
    private static string VolumeLess(string key)
    {
        var i = key.IndexOf("/:", StringComparison.Ordinal);
        return i >= 0 ? key[i..] : key;
    }

    // macOS hands out NFD file names and most other sources NFC, and a renamed
    // folder can differ only in case; neither makes it a different file.
    private static string LooseKey(string dirAndFile)
    {
        var s = dirAndFile.Replace("/:", "/");
        try { s = s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { /* invalid UTF-16: compare as is */ }
        return s.ToLowerInvariant();
    }

    private static string FileStem(string key)
    {
        var last = key.LastIndexOf("/:", StringComparison.Ordinal);
        var name = last >= 0 ? key[(last + 2)..] : key;
        return Path.GetFileNameWithoutExtension(name);
    }
}
