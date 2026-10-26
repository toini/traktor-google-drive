using TraktorGoogleDrive.Models;
using TraktorNmlParser.Models;
using Xunit;

namespace TraktorGoogleDrive.Tests;

/// <summary>
/// GigPreview.RangeFor (ADR 20261018-47 §4), in particular the entrance-loop rule:
/// a loop starting within 30 s after the preview start, or overlapping it, is where
/// the track gets mixed in, so it never ends the preview.
/// </summary>
public class GigPreviewTests
{
    private const double Duration = 420;

    private static CuePoint Load(double seconds) =>
        new() { Type = CuePointType.Load, Start = seconds * 1000, Hotcue = -1 };

    private static CuePoint Loop(double seconds, double lengthSeconds) =>
        new() { Type = CuePointType.Loop, Start = seconds * 1000, Length = lengthSeconds * 1000, Hotcue = -1 };

    private static CuePoint Cue(double seconds) =>
        new() { Type = CuePointType.Cue, Start = seconds * 1000, Hotcue = 0 };

    private static Track TrackWith(params CuePoint[] cues) => new() { CuePoints = cues.ToList() };

    [Fact]
    public void EntranceLoopThenExitLoop_EndsAtTheExitLoop()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(100), Loop(105, 8), Loop(280, 16)), Duration);

        Assert.Equal(100, range.Start);
        Assert.Equal(296, range.End);
        Assert.Equal("entrance loop ignored", range.Note);
    }

    [Fact]
    public void OnlyAnEntranceLoop_PlaysToTheEnd()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(100), Loop(105, 8)), Duration);

        Assert.Equal(100, range.Start);
        Assert.Null(range.End);
        Assert.Equal("entrance loop ignored — to end", range.Note);
    }

    // The boundary is exclusive for entrance loops: "within 30 s" means strictly
    // less than 30 s after the start, so a loop exactly 30 s after is an exit loop.
    [Fact]
    public void LoopExactly30sAfterTheStart_IsAnExitLoop()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(100), Loop(130, 4)), Duration);

        Assert.Equal(134, range.End);
        Assert.Null(range.Note);
    }

    [Fact]
    public void LoopJustUnder30sAfterTheStart_IsAnEntranceLoop()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(100), Loop(129.9, 4)), Duration);

        Assert.Null(range.End);
        Assert.Equal("entrance loop ignored — to end", range.Note);
    }

    [Fact]
    public void LoopOverlappingTheLoadCue_IsAnEntranceLoop()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(100), Loop(96, 8)), Duration);

        Assert.Null(range.End);
        Assert.Equal("entrance loop ignored — to end", range.Note);
    }

    [Fact]
    public void NoLoadCue_LoopAt10s_IsAnEntranceLoop()
    {
        var range = GigPreview.RangeFor(TrackWith(Loop(10, 8)), Duration);

        Assert.Equal(0, range.Start);
        Assert.Null(range.End);
        Assert.Equal("no load cue, entrance loop ignored — to end", range.Note);
    }

    // Toni's example: hotcue 1 at the start, the load cue about halfway, a 16-beat
    // loop right after it, hotcue 4 later. Preview used to stop a few seconds in.
    [Fact]
    public void GasChamberShape_PlaysFromTheLoadCueToTheEnd()
    {
        var range = GigPreview.RangeFor(
            TrackWith(Cue(0.5), Load(120), Loop(121.7, 6.857), Cue(180)), 240);

        Assert.Equal(120, range.Start);
        Assert.Null(range.End);
        Assert.Equal("entrance loop ignored — to end", range.Note);
    }

    [Fact]
    public void ExitLoopThatEndsLatestWins_AmongSeveral()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(0), Loop(200, 30), Loop(150, 8)), Duration);

        Assert.Equal(230, range.End);
    }

    [Fact]
    public void NoLoop_PlaysToTheEnd()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(40)), Duration);

        Assert.Equal(40, range.Start);
        Assert.Null(range.End);
        Assert.Equal("no loop — to end", range.Note);
    }

    [Fact]
    public void OnlyALoopEndingBeforeTheLoadCue_IsIgnored()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(100), Loop(20, 8)), Duration);

        Assert.Null(range.End);
        Assert.Equal("loop ends before load — to end", range.Note);
    }

    [Fact]
    public void ExitLoopPastTheDuration_PlaysToTheEnd()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(0), Loop(400, 30)), Duration);

        Assert.Null(range.End);
        Assert.Null(range.Note);
    }

    [Fact]
    public void Describe_ShowsTheEntranceLoopReason()
    {
        var range = GigPreview.RangeFor(TrackWith(Load(120), Loop(121.7, 6.857)), 240);

        Assert.Equal("2:00 → end · entrance loop ignored — to end", range.Describe());
    }
}
