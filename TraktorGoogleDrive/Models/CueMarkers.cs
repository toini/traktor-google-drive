using TraktorNmlParser.Models;

namespace TraktorGoogleDrive.Models;

/// <summary>
/// One NML cue as the waveform draws it. Times are seconds; <see cref="End"/> is
/// set only for loops, which are drawn as a region rather than a line.
/// </summary>
public record CueMarker(double Start, double? End, string Kind, string? Label);

/// <summary>
/// Turns Traktor's CUE_V2 entries into waveform markers. TYPE: 0 cue, 1 fade-in,
/// 2 fade-out, 3 load, 4 grid, 5 loop. START and LEN are milliseconds, HOTCUE is
/// a 0-based slot with -1 meaning "not a hotcue".
/// </summary>
public static class CueMarkers
{
    // Traktor writes these as the name of a cue nobody named, so showing them
    // would put the same word on every marker.
    private static readonly HashSet<string> PlaceholderNames =
        new(StringComparer.OrdinalIgnoreCase) { "n.n.", "AutoGrid" };

    public static IReadOnlyList<CueMarker> For(Track? track)
    {
        if (track is null) return [];

        var markers = new List<CueMarker>();
        foreach (var cue in track.CuePoints)
        {
            var kind = cue.Type switch
            {
                CuePointType.Cue => "cue",
                CuePointType.FadeIn or CuePointType.FadeOut => "fade",
                CuePointType.Load => "load",
                CuePointType.Loop => "loop",
                // The grid anchor sits on the first beat of every analysed track
                // and says nothing a listener needs.
                _ => null,
            };
            if (kind is null) continue;

            var start = cue.Start / 1000.0;
            double? end = kind == "loop" && cue.Length is > 0 ? start + cue.Length.Value / 1000.0 : null;
            markers.Add(new CueMarker(start, end, kind, LabelFor(cue, track.Bpm)));
        }
        return markers;
    }

    private static string? LabelFor(CuePoint cue, float? bpm)
    {
        var parts = new List<string>();
        if (cue.Hotcue >= 0) parts.Add((cue.Hotcue + 1).ToString());
        if (!string.IsNullOrWhiteSpace(cue.Name) && !PlaceholderNames.Contains(cue.Name)) parts.Add(cue.Name);
        if (cue.Type == CuePointType.Loop && cue.Length is > 0) parts.Add(LoopLength(cue.Length.Value, bpm));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Loop length the way a DJ reads it: in beats when the tempo is known.</summary>
    public static string LoopLength(double lengthMs, float? bpm)
    {
        if (bpm is > 0)
        {
            var beats = lengthMs / 1000.0 * bpm.Value / 60.0;
            var rounded = Math.Round(beats);
            return Math.Abs(beats - rounded) < 0.05
                ? $"{rounded:0} {(rounded == 1 ? "beat" : "beats")}"
                : $"{beats:0.#} beats";
        }
        return $"{lengthMs / 1000.0:0.#} s";
    }
}

/// <summary>Where gig preview plays a track from and to, plus why when a default applied.</summary>
public record PreviewRange(double Start, double? End, string? Note)
{
    public string Describe() =>
        $"{TrackTime.Position(Start)} → {(End is { } e ? TrackTime.Position(e) : "end")}"
        + (Note is null ? "" : $" · {Note}");
}

/// <summary>
/// The gig-preview rule from ADR 20261018-47 §4: start at the earliest load cue,
/// stop at the end of the exit loop that ends latest, with defaults for the cases
/// a real collection is full of.
/// </summary>
public static class GigPreview
{
    /// <summary>
    /// A loop that starts less than this many seconds after the preview start, or
    /// that overlaps the start, is where Toni mixes the track IN (an entrance
    /// loop), so it never ends the preview. A loop starting exactly this far after
    /// the start already counts as an exit loop.
    /// </summary>
    public const double EntranceLoopWindowSeconds = 30;

    public static PreviewRange RangeFor(Track? track, double durationSeconds)
    {
        var cues = track?.CuePoints ?? [];

        // Earliest, not the first in the file: 43 tracks in a real collection
        // carry more than one load marker.
        var load = cues.Where(c => c.Type == CuePointType.Load).Select(c => c.Start / 1000.0)
            .DefaultIfEmpty(double.NaN).Min();
        var hasLoad = !double.IsNaN(load);
        // No load marker: Traktor itself loads such a track at 0.
        var start = hasLoad ? load : 0;
        if (durationSeconds > 0 && start >= durationSeconds) start = 0;

        var loops = cues.Where(c => c.Type == CuePointType.Loop && c.Length is > 0)
            .Select(c => (Start: c.Start / 1000.0, End: (c.Start + c.Length!.Value) / 1000.0))
            .ToList();

        // Exit loops start at least the window after the start. Everything else
        // either overlaps the start or starts just after it (an entrance loop), or
        // ends at or before it (almost certainly stale).
        var exitLoops = loops.Where(l => l.Start - start >= EntranceLoopWindowSeconds).ToList();
        var hasEntrance = loops.Any(l => l.Start - start < EntranceLoopWindowSeconds && l.End > start);

        var startNote = hasLoad ? null : "no load cue";
        var entranceNote = hasEntrance ? "entrance loop ignored" : null;

        if (loops.Count == 0)
            return new PreviewRange(start, null, Join(startNote, "no loop — to end"));

        if (exitLoops.Count == 0)
            return new PreviewRange(start, null, Join(startNote, hasEntrance
                ? "entrance loop ignored — to end"
                // Almost certainly a stale loop; playing on is safer than skipping.
                : "loop ends before load — to end"));

        var end = exitLoops.Max(l => l.End);

        // Past the end is the same as "to the end", and the element's own ended
        // event is the reliable signal for that.
        if (durationSeconds > 0 && end >= durationSeconds)
            return new PreviewRange(start, null, Join(startNote, entranceNote));

        return new PreviewRange(start, end, Join(startNote, entranceNote));
    }

    private static string? Join(string? a, string? b) =>
        a is null ? b : b is null ? a : $"{a}, {b}";
}
