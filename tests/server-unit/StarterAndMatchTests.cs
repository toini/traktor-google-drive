using TraktorGoogleDrive.Discover;

namespace TraktorGoogleDrive.Tests;

public class WatermarkTests
{
    [Theory]
    [InlineData("2026-10-20", "2026-10-25", true)]
    [InlineData("2026-10-25", "2026-10-25", true)]
    [InlineData("2026-10-26", "2026-10-25", false)]   // later in October still appears with a day watermark
    [InlineData("2026-10-31", "2026-10", true)]       // a month covers its last day (OneNote precision)
    [InlineData("2026-11-01", "2026-10", false)]
    public void Before(string date, string watermark, bool before) =>
        Assert.Equal(before, DiscoverState.BeforeWatermark(DateOnly.Parse(date), watermark));

    [Fact]
    public void A_day_raises_a_month_only_when_it_is_later()
    {
        var seed = new DiscoverEvent { Type = "seed-add", At = DateTimeOffset.UnixEpoch, SeedKey = "s", Kind = SeedKind.Label, Name = "L", Genre = "H" };
        DiscoverEvent Mark(string m, int s) => new() { Type = "watermark", At = DateTimeOffset.UnixEpoch.AddSeconds(s), SeedKey = "s", Month = m };

        Assert.Equal("2026-10", DiscoverState.Fold([seed, Mark("2026-10", 1), Mark("2026-10-25", 2)]).Seeds["s"].Watermark);
        Assert.Equal("2026-10-25", DiscoverState.Fold([seed, Mark("2026-09", 1), Mark("2026-10-25", 2)]).Seeds["s"].Watermark);
    }
}

public class ReleaseMatchTests
{
    private static bool Same(string t1, string? l1, string? a1, string? c1, string t2, string? l2, string? a2) =>
        ReleaseMatch.From(t1, l1, a1, c1)!.Matches(ReleaseMatch.From(t2, l2, a2, null)!);

    [Fact]
    public void The_same_release_on_another_store()
    {
        Assert.True(Same("Toxic Compilation 2025 V.1", null, "Various Artists", null, "Toxic Compilation 2025, Vol. 1", "Toxic Records", "Various Artists"));
        Assert.True(Same("Terror Protocol EP", "Darker Sounds", null, "DS054", "DS054 - Terror Protocol EP", "Darker Sounds", "Hefty"));
        Assert.True(Same("x", "Erebos Records", null, "ER031", "ER31 Source Of Darkness", "Erebos Records", null));
    }

    [Fact]
    public void Not_the_same()
    {
        Assert.False(Same("Feeding Generation Z", "Carnivor Records", null, null, "Feeding Generation Z II", "Carnivor Records", null));
        Assert.False(Same("Toxic Compilation 2025 V.1", null, null, null, "Toxic Compilation 2025 V.2", null, null));
        Assert.False(Same("Elegies", null, null, null, "Elegies", null, null));   // too short without a label
        Assert.False(Same("Terror Protocol EP", "Darker Sounds", null, "DS054", "DS055 - Terror Protocol EP", "Darker Sounds", null));
    }

    [Fact]
    public void A_heard_release_from_beatport_hides_the_same_release_from_bandcamp()
    {
        var state = DiscoverState.Fold([new DiscoverEvent
        {
            Type = "release-heard", At = DateTimeOffset.UnixEpoch, ReleaseUrl = "beatport:4930160",
            ReleaseTitle = "Toxic Compilation 2025 V.1", Artist = "Various Artists",
        }]);
        Assert.True(state.IsReleaseHeard("https://toxic.bandcamp.com/album/toxic-compilation-2025-vol-1", "Toxic Compilation 2025 Vol. 1", "Toxic", "Various Artists"));
        Assert.False(state.IsReleaseHeard("https://toxic.bandcamp.com/album/other", "Toxic Compilation 2026", "Toxic", null));
    }
}

public class StarterListTests
{
    private static StarterFile Real() =>
        StarterFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "starter.json")))!;

    [Fact]
    public void The_shipped_starter_list_parses_and_applies_once()
    {
        var file = Real();
        var state = new DiscoverState();
        foreach (var e in file.EventsFor(state)) state.Apply(e);

        var seed = state.Seeds[SeedCandidate.SeedKey(SeedKind.Label, "Carnivor Records", "H")];
        Assert.Equal("2026-10-25", seed.Watermark);
        Assert.Equal("https://carnivorrecords.bandcamp.com", seed.BandcampUrl);
        Assert.Equal(SeedOrigin.EtiLisaa, seed.Origin);

        Assert.Contains("https://carnivorrecords.bandcamp.com/album/feeding-generation-z-ii", state.HeardReleases);
        Assert.Contains("https://carnivorrecords.bandcamp.com/album/feeding-generation-z", state.HeardReleases);
        Assert.Contains("https://carnivorrecords.bandcamp.com/album/elegies", state.HeardReleases);
        Assert.Contains("https://carnivorrecords.bandcamp.com/album/19th-century", state.HeardReleases);
        Assert.Contains("beatport:4930160", state.HeardReleases);

        var picks = state.Tracks.Values.ToList();
        Assert.Equal(3, picks.Count);
        Assert.All(picks, t =>
        {
            Assert.Equal(Pass1.Maybe, t.Pass1);
            Assert.Equal("H", t.Genre);
            Assert.Null(t.Decision);
        });
        Assert.Contains(picks, t => t.TrackUrl == "https://carnivorrecords.bandcamp.com/track/madtype-shadym-t-y-remix");

        Assert.Empty(file.EventsFor(state));   // never twice
    }

    [Fact]
    public void A_change_made_after_the_starter_is_never_undone()
    {
        var file = Real();
        var events = file.EventsFor(new DiscoverState()).Select((e, i) => e with { At = DateTimeOffset.UnixEpoch.AddSeconds(i) }).ToList();
        var key = DiscoverState.UrlTrackKey("https://carnivorrecords.bandcamp.com/track/satanas");
        events.Add(new DiscoverEvent { Type = "track", At = DateTimeOffset.UnixEpoch.AddHours(1), TrackKey = key, Decision = Decision.Skip });
        var state = DiscoverState.Fold(events);

        Assert.Empty(file.EventsFor(state));
        Assert.Equal(Decision.Skip, state.Tracks[key].Decision);
    }

    [Fact]
    public void A_whole_heard_release_never_queues_and_a_url_known_pick_stays_heard()
    {
        var state = new DiscoverState();
        foreach (var e in Real().EventsFor(state)) state.Apply(e);
        var seedKey = SeedCandidate.SeedKey(SeedKind.Label, "Carnivor Records", "H");
        state.Apply(new DiscoverEvent { Type = "watermark", At = DateTimeOffset.UnixEpoch, SeedKey = seedKey, Month = "2020-01", Clear = true });

        ReleaseInfo R(string slug, string title, string date, params string[] trackSlugs) => new(
            "bandcamp", $"https://carnivorrecords.bandcamp.com/album/{slug}", title, "VA", "Carnivor Records", DateOnly.Parse(date), [],
            trackSlugs.Select((s, i) => new ReleaseTrack(100 + i + title.Length * 10, i + 1, s, "X", 300,
                $"https://carnivorrecords.bandcamp.com/track/{s}", true)).ToList(),
            seedKey);

        var q = QueueBuilder.Build(state, "H",
            [R("feeding-generation-z-ii", "Feeding Generation Z II", "2024-03-01", "satanas", "hunger"),
             R("new-one", "New One", "2026-11-02", "satanas", "fresh")], new OwnedIndex([]));

        Assert.Equal(["fresh"], q.Select(i => i.Track.Title));
    }
}
