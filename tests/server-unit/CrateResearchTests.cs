using TraktorGoogleDrive.Discover;
using TraktorGoogleDrive.Services;

namespace TraktorGoogleDrive.Tests;

/// <summary>
/// Buy links found outside the app, shipped as a starter batch of "crate-resolve"
/// events (via "research"); Beatport as a buy link; the verdict badges and chips.
/// </summary>
public class CrateResearchTests
{
    private const string Crate = "c1";
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    private static StarterBatch.CrateEntry List(params (string Artist, string Title)[] tracks) =>
        new(Crate, "Set", "P", null, tracks.Select(t => new StarterBatch.CrateTrackEntry(t.Artist, t.Title, null)).ToList());

    private static StarterBatch Research(string version, params StarterBatch.ResolutionEntry[] entries) =>
        new(version, null, null, null, null, entries);

    private static StarterBatch.ResolutionEntry Bc(string artist, string title, string url = "https://skogarna.bandcamp.com/track/x") =>
        new(Crate, artist, title, Bandcamp: url, Label: "Skogarna", Confidence: "high");

    private static void ApplyAll(DiscoverState state, StarterFile file)
    {
        var n = 0;
        foreach (var e in file.EventsFor(state)) state.Apply(e with { At = At.AddSeconds(++n + state.StarterVersions.Count * 1000) });
    }

    private static CrateItem Item(DiscoverState state, string artist, string title) =>
        state.Crates[Crate].Items.Single(i => i.Key == CrateMatch.ItemKey(Crate, artist, title));

    [Fact]
    public void Research_resolves_items_added_by_an_earlier_batch_of_the_same_run_once()
    {
        var file = new StarterFile([
            new StarterBatch("1", null, null, null, [List(("A", "One"), ("B", "Two"), ("C", "Three"))]),
            Research("2",
                Bc("A", "One"),
                new StarterBatch.ResolutionEntry(Crate, "B", "Two", Beatport: "https://www.beatport.com/track/two/123456", Confidence: "medium"),
                new StarterBatch.ResolutionEntry(Crate, "C", "Three", Note: "nothing found")),
        ]);
        var state = new DiscoverState();
        ApplyAll(state, file);

        var one = Item(state, "A", "One").Resolution!;
        Assert.Equal(("bandcamp", "research", "https://skogarna.bandcamp.com/track/x"), (one.Source, one.Via, one.PageUrl));
        Assert.Null(one.TrackId);   // read on Listen
        Assert.True(CrateService.NeedsReading(Item(state, "A", "One")));
        var two = Item(state, "B", "Two").Resolution!;
        Assert.Equal(("beatport", "https://www.beatport.com/track/two/123456"), (two.Source, two.BeatportUrl));
        Assert.NotNull(two.Message);   // not certain: says so
        // No URL: not found, in the search's words, so Find all leaves it alone.
        Assert.Equal((CrateStatus.NotFound, "nothing found"), (Item(state, "C", "Three").Resolution!.Status, Item(state, "C", "Three").Resolution!.Message));
        Assert.Contains("2", state.StarterVersions);
        Assert.Empty(file.EventsFor(state));   // applied once
    }

    [Fact]
    public void Research_never_overrides_what_Toni_found_pasted_picked_or_judged()
    {
        var state = new DiscoverState();
        ApplyAll(state, new StarterFile([new StarterBatch("1", null, null, null,
            [List(("A", "Found"), ("B", "Pasted"), ("C", "Rated"), ("D", "Missed"), ("E", "Skipped"))])]));
        void Resolve(string artist, string title, CrateResolution r) =>
            state.Apply(new DiscoverEvent { Type = "crate-resolve", At = At.AddDays(1), TrackKey = CrateMatch.ItemKey(Crate, artist, title), Resolution = r });
        Resolve("A", "Found", new CrateResolution { Status = CrateStatus.Found, Source = "bandcamp", PageUrl = "https://a.bandcamp.com/track/found", TrackId = 1, Via = "discogs" });
        Resolve("B", "Pasted", new CrateResolution { Status = CrateStatus.Found, Source = "beatport", PageUrl = "https://www.beatport.com/track/p/1", Via = "pasted" });
        Resolve("D", "Missed", new CrateResolution { Status = CrateStatus.NotFound, Message = "Not found yet." });
        state.Apply(new DiscoverEvent { Type = "track", At = At.AddDays(1), TrackKey = CrateMatch.ItemKey(Crate, "C", "Rated"), Rating = 4 });
        state.Apply(new DiscoverEvent { Type = "track", At = At.AddDays(1), TrackKey = CrateMatch.ItemKey(Crate, "E", "Skipped"), Pass1 = Pass1.Skip });

        var research = Research("2", Bc("A", "Found"), Bc("B", "Pasted"), Bc("C", "Rated"), Bc("D", "Missed"), Bc("E", "Skipped"), Bc("Z", "Not in the list"));
        var events = research.EventsFor(state);

        Assert.Equal([CrateMatch.ItemKey(Crate, "D", "Missed")], events.Where(e => e.Type == "crate-resolve").Select(e => e.TrackKey));
        Assert.Equal("starter", events[^1].Type);
    }

    [Fact]
    public void A_research_event_folded_after_Toni_s_paste_from_another_device_loses()
    {
        var key = CrateMatch.ItemKey(Crate, "A", "One");
        var state = DiscoverState.Fold([
            new DiscoverEvent { Type = "crate-add", At = At, CrateId = Crate, Name = "Set", Genre = "P" },
            new DiscoverEvent { Type = "crate-item", At = At, CrateId = Crate, TrackKey = key, Artist = "A", Title = "One" },
            new DiscoverEvent { Type = "crate-resolve", At = At.AddSeconds(1), TrackKey = key,
                Resolution = new CrateResolution { Status = CrateStatus.Found, Source = "bandcamp", PageUrl = "https://mine.bandcamp.com/track/one", TrackId = 9, Via = "pasted" } },
            new DiscoverEvent { Type = "crate-resolve", At = At.AddSeconds(2), TrackKey = key,
                Resolution = StarterBatch.Resolution(Bc("A", "One")) },
        ]);
        Assert.Equal("https://mine.bandcamp.com/track/one", state.Crates[Crate].Items[0].Resolution!.PageUrl);
    }

    [Fact]
    public void The_shipped_research_batch_resolves_the_crate_and_corrects_its_titles_once()
    {
        var file = StarterFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "starter.json")))!;
        var state = new DiscoverState();
        foreach (var e in file.EventsFor(state)) state.Apply(e);
        var crate = state.Crates["looney-boon-hug-halloween-2026"];
        CrateItem ByKey(string artist, string title) => crate.Items.Single(i => i.Key == CrateMatch.ItemKey(crate.Id, artist, title));

        Assert.Equal(70, crate.Items.Count);
        Assert.Equal("https://muscaria1.bandcamp.com/track/bioport", ByKey("Ianuaria & Muscaria", "Bioport").Resolution!.PageUrl);
        Assert.Equal("Self Realization", ByKey("Seraburayka & Fear Disorder", "Self R… (truncated)").Title);
        var ululation = ByKey("Into The Screaming Abyss", "Ululation");
        Assert.Equal(("Pepe's Bodega", "Into The Screaming Abyss - Ululation"), (ululation.Artist, ululation.Title));
        Assert.Contains("screenshot", ululation.Note);
        Assert.Equal("Swamp Encounter", ByKey("The Hierophants (Via Axis, Purist & Yachay)", "(truncated)").Title);
        Assert.Equal("The Cosmos Welcomes You", ByKey("NoizeBug", "The Cosmos Welcomes You (Psilocybin Intellig… truncated)").Title);
        Assert.Equal("Not Your Kind!", ByKey("Koobluda", "NOT UR KIND!").Title);
        var gidra = ByKey("Gidra", "Spider Skunk (Edit 4)");
        Assert.Equal(CrateStatus.NotFound, gidra.Resolution!.Status);
        Assert.StartsWith("https://beatspace-parvati.bandcamp.com/", gidra.Link);
        Assert.Equal(3, crate.Items.Count(i => i.Resolution?.Free == true || i.Note?.Contains("Free on") == true));
        Assert.True(crate.Items.Count(i => i.Resolution is { Status: CrateStatus.Found, Via: "research" }) >= 45);
        Assert.DoesNotContain(state.Tracks.Keys, k => k.StartsWith("crate:"));   // no verdicts shipped
        Assert.Empty(file.EventsFor(state));
    }

    [Theory]
    [InlineData("https://www.beatport.com/track/patch-hell/19283746", true)]
    [InlineData("https://beatport.com/release/testimony-20/4930160", true)]
    [InlineData("https://www.beatport.com/de/track/x/1", true)]
    [InlineData("https://www.beatport.com/search?q=x", false)]
    [InlineData("http://www.beatport.com/track/x/1", false)]
    [InlineData("https://beatport.com.evil.example/track/x/1", false)]
    public void Beatport_track_and_release_urls_are_recognised(string url, bool ok) =>
        Assert.Equal(ok, CrateMatch.IsBeatportUrl(url));

    private static CrateItem NewItem() => new() { Key = CrateMatch.ItemKey(Crate, "A", "One"), CrateId = Crate, Artist = "A", Title = "One" };

    [Fact]
    public async Task A_pasted_Beatport_url_is_the_buy_link_and_is_never_fetched()
    {
        var fake = new FakeLookup();
        var r = await new CrateResolver(fake).FromUrlAsync(NewItem(), "https://www.beatport.com/track/one/42", _ => { }, default, soundCloud: false);
        Assert.Equal((CrateStatus.Found, "beatport", "https://www.beatport.com/track/one/42", "pasted"), (r.Status, r.Source, r.BeatportUrl, r.Via));
        Assert.Empty(fake.Calls);

        // Onto a Bandcamp find: added beside it, the Bandcamp stream stays.
        var item = NewItem();
        item.Resolution = new CrateResolution { Status = CrateStatus.Found, Source = "bandcamp", PageUrl = "https://a.bandcamp.com/track/one", TrackId = 5 };
        var both = await new CrateResolver(fake).FromUrlAsync(item, "https://www.beatport.com/track/one/42", _ => { }, default);
        Assert.Equal(("bandcamp", 5L, "https://www.beatport.com/track/one/42"), (both.Source, both.TrackId!.Value, both.BeatportUrl));

        await Assert.ThrowsAsync<DiscoverApiException>(() =>
            new CrateResolver(fake).FromUrlAsync(NewItem(), "https://www.beatport.com/search?q=one", _ => { }, default));
    }

    [Fact]
    public async Task With_SoundCloud_off_a_SoundCloud_url_is_refused_without_a_request()
    {
        var fake = new FakeLookup();
        var e = await Assert.ThrowsAsync<DiscoverApiException>(() =>
            new CrateResolver(fake).FromUrlAsync(NewItem(), "https://soundcloud.com/a/one", _ => { }, default, soundCloud: false));
        Assert.Equal("Paste a Bandcamp or Beatport URL", e.Message);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Not_found_words_name_no_Discogs()
    {
        var r = await new CrateResolver(new FakeLookup()).ResolveAsync(NewItem(), _ => { }, default);
        Assert.Equal(CrateStatus.NotFound, r.Status);
        Assert.DoesNotContain("Discogs", r.Message);
        Assert.StartsWith("Not found yet", r.Message);
    }

    [Fact]
    public void Verdicts_read_as_one_badge_and_filter_into_chips()
    {
        TrackState T(Pass1? p = null, int? rating = null, Decision? d = null, bool bought = false) =>
            new() { Key = "k", Pass1 = p, Rating = rating, Decision = d, Bought = bought, Heard = true };

        Assert.Equal(CrateVerdict.Unheard, CrateVerdicts.Of(null));
        Assert.Equal(CrateVerdict.Unheard, CrateVerdicts.Of(T()));   // undone: heard, no verdict
        Assert.Equal(CrateVerdict.Skipped, CrateVerdicts.Of(T(Pass1.Skip)));
        Assert.Equal(CrateVerdict.Maybe, CrateVerdicts.Of(T(Pass1.Maybe)));
        Assert.Equal(CrateVerdict.Liked, CrateVerdicts.Of(T(Pass1.Like)));
        Assert.Equal(CrateVerdict.Rated, CrateVerdicts.Of(T(Pass1.Like, 4)));
        Assert.Equal(CrateVerdict.Buy, CrateVerdicts.Of(T(Pass1.Like, 4, Decision.Buy)));
        Assert.Equal(CrateVerdict.Bought, CrateVerdicts.Of(T(Pass1.Like, 4, Decision.Buy, true)));
        Assert.Equal("★4", CrateVerdicts.Badge(CrateVerdict.Rated, 4));

        Assert.True(CrateVerdicts.Matches(CrateFilter.ToHear, CrateItemState.NotFound, CrateVerdict.Unheard));
        Assert.False(CrateVerdicts.Matches(CrateFilter.ToHear, CrateItemState.Found, CrateVerdict.Skipped));
        Assert.False(CrateVerdicts.Matches(CrateFilter.ToHear, CrateItemState.Owned, CrateVerdict.Unheard));
        Assert.True(CrateVerdicts.Matches(CrateFilter.Liked, CrateItemState.Found, CrateVerdict.Rated));
        Assert.True(CrateVerdicts.Matches(CrateFilter.NotFound, CrateItemState.Pending, CrateVerdict.Unheard));
        Assert.False(CrateVerdicts.Matches(CrateFilter.NotFound, CrateItemState.Found, CrateVerdict.Unheard));
    }
}
