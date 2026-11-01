using TraktorGoogleDrive.Discover;
using TraktorGoogleDrive.Services;

namespace TraktorGoogleDrive.Tests;

/// <summary>A lookup that answers from a table and records what was asked: no network.</summary>
internal sealed class FakeLookup : ICrateLookup
{
    public List<string> Calls { get; } = [];
    public Dictionary<string, DiscogsLookupDto> Discogs { get; } = new();
    public Dictionary<string, List<DiscoverApi.ReleaseRef>> Lists { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, DiscoverApi.Release> Releases { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SoundCloudTrackDto> SoundCloud { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int? DiscogsStatus { get; set; }

    public Task<DiscogsLookupDto> DiscogsAsync(string artist, string title, long? releaseId, CancellationToken ct)
    {
        Calls.Add($"discogs {artist} / {title}{(releaseId is { } r ? $" #{r}" : "")}");
        if (DiscogsStatus is { } s) throw new DiscoverApiException(s, $"HTTP {s}");
        return Task.FromResult(Discogs.TryGetValue($"{title}{(releaseId is { } id ? "#" + id : "")}", out var hit)
            ? hit : new DiscogsLookupDto(artist, title, [], false, null));
    }

    public Task<DiscoverApi.ReleasesResponse> BandcampListAsync(string pageUrl, CancellationToken ct)
    {
        Calls.Add("list " + pageUrl);
        var key = pageUrl.Replace("/music", "").TrimEnd('/');
        return Lists.TryGetValue(key, out var refs)
            ? Task.FromResult(new DiscoverApi.ReleasesResponse("bandcamp", key + "/music", refs))
            : Task.FromException<DiscoverApi.ReleasesResponse>(new DiscoverApiException(404, "Bandcamp has no such page"));
    }

    public Task<DiscoverApi.Release> BandcampReleaseAsync(string releaseUrl, CancellationToken ct)
    {
        Calls.Add("release " + releaseUrl);
        return Releases.TryGetValue(releaseUrl, out var r)
            ? Task.FromResult(r)
            : Task.FromException<DiscoverApi.Release>(new DiscoverApiException(404, "Bandcamp has no such page"));
    }

    public Task<SoundCloudTrackDto?> SoundCloudAsync(string? url, string artist, string title, string? uploader, CancellationToken ct)
    {
        Calls.Add($"soundcloud {url ?? $"{uploader}/{title}"}");
        return Task.FromResult(SoundCloud.GetValueOrDefault(url ?? title));
    }

    public static DiscoverApi.Release Release(string url, string title, string label, params (long Id, string Title, string Artist)[] tracks) =>
        new("bandcamp", url, "album", 1, title, "VA", label, null, DateTimeOffset.Parse("2025-10-31T00:00:00Z"), [],
            tracks.Select((t, i) => new DiscoverApi.Track(t.Id, i + 1, t.Title, t.Artist, 420, url.Replace("/album/", "/track/") + "-" + i, null, true)).ToList(),
            null, 3);
}

public class CrateModelTests
{
    private static StarterFile Real() =>
        StarterFile.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "starter.json")))!;

    [Fact]
    public void The_shipped_crate_lands_under_P_with_all_70_tracks_once()
    {
        var state = new DiscoverState();
        var file = Real();
        foreach (var e in file.EventsFor(state)) state.Apply(e);

        var crate = Assert.Single(state.CratesOf("P"));
        Assert.Equal("Looney Boon Hug Halloween 2026", crate.Name);
        Assert.Equal(70, crate.Items.Count);
        Assert.Equal(70, crate.Items.Select(i => i.Key).Distinct().Count());
        Assert.Equal("Tengri & Jahbo", crate.Items[0].Artist);
        Assert.Equal("Patch Hell", crate.Items[0].Title);
        Assert.Equal("Tengri, Jahbo", crate.Items[0].Uploader);
        Assert.Equal("Farebi Jalebi", crate.Items[^1].Artist);
        Assert.Empty(state.CratesOf("H"));
        // Nothing of it is a seed, a track to triage or a heard release of P.
        Assert.DoesNotContain(state.Seeds.Values, s => s.Genre == "P");
        Assert.DoesNotContain(state.Tracks.Keys, k => k.StartsWith("crate:"));
        Assert.Empty(file.EventsFor(state));
    }

    [Fact]
    public void Crate_triage_never_reaches_the_genre_shortlist()
    {
        var state = new DiscoverState();
        foreach (var e in Real().EventsFor(state)) state.Apply(e);
        var item = state.Crates["looney-boon-hug-halloween-2026"].Items[0];
        state.Apply(new DiscoverEvent { Type = "track", At = DateTimeOffset.UnixEpoch, TrackKey = item.Key, Pass1 = Pass1.Like, Decision = Decision.Buy });

        Assert.Equal("looney-boon-hug-halloween-2026", CrateMatch.CrateOf(state.Tracks[item.Key].Key));
        Assert.Null(state.Tracks[item.Key].Genre);   // DiscoverService.Shortlist("P") filters on Genre == "P"
        Assert.Empty(QueueBuilder.Build(state, "P", [], new OwnedIndex([])));
    }

    [Fact]
    public void Create_rename_delete_and_resolve_fold_from_events()
    {
        var at = DateTimeOffset.UnixEpoch;
        var state = DiscoverState.Fold([
            new DiscoverEvent { Type = "crate-add", At = at, CrateId = "c1", Name = "Set A", Genre = "Z" },
            new DiscoverEvent { Type = "crate-item", At = at.AddSeconds(1), CrateId = "c1", TrackKey = "crate:c1:a--b", Artist = "A", Title = "B", Pos = 0 },
            new DiscoverEvent { Type = "crate-rename", At = at.AddSeconds(2), CrateId = "c1", Name = "Set A (gig)" },
            new DiscoverEvent { Type = "crate-resolve", At = at.AddSeconds(3), TrackKey = "crate:c1:a--b",
                Resolution = new CrateResolution { Status = CrateStatus.Found, Source = "soundcloud", PageUrl = "https://soundcloud.com/a/b" } },
        ]);
        var crate = state.Crates["c1"];
        Assert.Equal("Set A (gig)", crate.Name);
        Assert.Equal("soundcloud", crate.Items[0].Resolution!.Source);

        state.Apply(new DiscoverEvent { Type = "crate-resolve", At = at.AddSeconds(4), TrackKey = "crate:c1:a--b", Clear = true });
        Assert.Null(crate.Items[0].Resolution);
        state.Apply(new DiscoverEvent { Type = "crate-remove", At = at.AddSeconds(5), CrateId = "c1" });
        Assert.Empty(state.CratesOf("Z"));
    }

    [Fact]
    public void A_resolution_survives_the_log_round_trip()
    {
        var e = new DiscoverEvent
        {
            Type = "crate-resolve", At = DateTimeOffset.UnixEpoch, TrackKey = "crate:c:x--y",
            Resolution = new CrateResolution
            {
                Status = CrateStatus.Ambiguous,
                Candidates = [new CrateCandidate("discogs", "Patch Hell", "Tengri (2)", "Forest Label", "2024", DiscogsId: 42)],
            },
        };
        var back = DiscoverStore.Parse(DiscoverStore.Serialize([e])).Single();
        Assert.Equal(CrateStatus.Ambiguous, back.Resolution!.Status);
        Assert.Equal(42, back.Resolution.Candidates![0].DiscogsId);
    }

    [Theory]
    [InlineData("Self R… (truncated)", "Self R")]
    [InlineData("The Cosmos Welcomes You (Psilocybin Intellig… truncated)", "The Cosmos Welcomes You")]
    [InlineData("Wagwan (Original Mix)", "Wagwan (Original Mix)")]
    [InlineData("(truncated)", "")]
    public void Truncated_titles_search_by_what_is_left(string title, string expected) =>
        Assert.Equal(expected, CrateMatch.SearchTitle(title));

    [Fact]
    public void Guesses_are_the_artist_then_the_uploader()
    {
        var item = new CrateItem { Key = "k", CrateId = "c", Artist = "Vahati & Creepy Deep", Title = "Black Moon", Uploader = "Karawara Records" };
        Assert.Equal(["https://vahati.bandcamp.com", "https://karawararecords.bandcamp.com"], CrateMatch.BandcampGuesses(item));
        var bosmer = new CrateItem { Key = "k", CrateId = "c", Artist = "Bosmer", Title = "Arrow to the Knee", Uploader = "Bosmer - Pralayah Records" };
        Assert.Equal(["https://bosmer.bandcamp.com", "https://pralayahrecords.bandcamp.com"], CrateMatch.BandcampGuesses(bosmer, 3));
    }

    [Fact]
    public void Bandcamp_search_opens_in_a_tab_with_artist_and_title()
    {
        var item = new CrateItem { Key = "k", CrateId = "c", Artist = "Raveen ft. Mafia", Title = "Buried Tech" };
        Assert.Equal("https://bandcamp.com/search?item_type=t&q=Raveen%20Buried%20Tech", CrateMatch.BandcampSearchUrl(item));
    }
}

public class CrateResolverTests
{
    private static CrateItem Item(string artist, string title, string? uploader = null) =>
        new() { Key = CrateMatch.ItemKey("c", artist, title), CrateId = "c", Artist = artist, Title = title, Uploader = uploader };

    private static DiscogsLookupDto Discogs(string title, string release, params string[] bandcamp) =>
        new("x", title, [new DiscogsCandidateDto(7, "Tengri", release, "2025", ["Forest Label"], "FL1", ["File"], "https://www.discogs.com/release/7", 6)],
            false, new DiscogsMatchDto(7, release, "Tengri", ["Forest Label"], "2025-10-31", "https://www.discogs.com/release/7", true, title, bandcamp.ToList()));

    [Fact]
    public async Task Discogs_gives_the_label_page_and_the_track_is_found_on_its_release()
    {
        var fake = new FakeLookup();
        fake.Discogs["Patch Hell"] = Discogs("Patch Hell", "Hell Patches EP", "https://forestlabel.bandcamp.com/music");
        fake.Lists["https://forestlabel.bandcamp.com"] =
        [
            new("https://forestlabel.bandcamp.com/album/other", "album", 1, "Other", null),
            new("https://forestlabel.bandcamp.com/album/hell-patches-ep", "album", 2, "Hell Patches EP", "Tengri"),
        ];
        fake.Releases["https://forestlabel.bandcamp.com/album/hell-patches-ep"] = FakeLookup.Release(
            "https://forestlabel.bandcamp.com/album/hell-patches-ep", "Hell Patches EP", "Forest Label",
            (11, "Intro", "Tengri"), (12, "Patch Hell", "Tengri & Jahbo"));

        var r = await new CrateResolver(fake).ResolveAsync(Item("Tengri & Jahbo", "Patch Hell"), _ => { }, default);

        Assert.Equal(CrateStatus.Found, r.Status);
        Assert.Equal("bandcamp", r.Source);
        Assert.Equal(12, r.TrackId);
        Assert.Equal("discogs", r.Via);
        Assert.Equal("Forest Label", r.Label);
        Assert.Equal("https://www.discogs.com/release/7", r.DiscogsUrl);
        // Only the release whose title fits was opened; Bandcamp's search was never asked.
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/album/other"));
        Assert.DoesNotContain(fake.Calls, c => c.Contains("/search"));
    }

    [Fact]
    public async Task Without_Discogs_a_checked_guess_of_the_artist_page_finds_a_compilation_track_titled_artist_dash_title()
    {
        var fake = new FakeLookup();
        fake.Lists["https://atriohm.bandcamp.com"] = [new("https://atriohm.bandcamp.com/album/shesharka", "album", 1, "Shesharka", "Atriohm")];
        fake.Releases["https://atriohm.bandcamp.com/album/shesharka"] = FakeLookup.Release(
            "https://atriohm.bandcamp.com/album/shesharka", "Shesharka", "Atriohm", (5, "Atriohm - Shesharka", "Various"));

        var r = await new CrateResolver(fake).ResolveAsync(Item("Atriohm", "Shesharka", "Atriohm"), _ => { }, default);
        Assert.Equal(CrateStatus.Found, r.Status);
        Assert.Equal("guess", r.Via);
        Assert.Equal(5, r.TrackId);
    }

    [Fact]
    public async Task A_wrong_guess_is_not_found_and_says_what_was_tried()
    {
        var fake = new FakeLookup();
        var r = await new CrateResolver(fake).ResolveAsync(Item("Nobot", "The Future", "Nobot"), _ => { }, default);
        Assert.Equal(CrateStatus.NotFound, r.Status);
        Assert.Contains("nobot.bandcamp.com", r.Message);
        Assert.Single(fake.Calls, c => c.StartsWith("list "));   // uploader and artist are the same page
    }

    [Fact]
    public async Task Ambiguous_discogs_artists_ask_Toni_to_pick_and_a_pick_resolves_through_that_release()
    {
        var fake = new FakeLookup();
        fake.Discogs["The Future"] = new("Nobot", "The Future",
        [
            new(1, "Nobot", "The Future", "2024", ["Label A"], null, ["File"], "https://www.discogs.com/release/1", 6),
            new(2, "Nobot (2)", "Futures", "2019", ["Label B"], null, ["File"], "https://www.discogs.com/release/2", 6),
        ], true, null);
        fake.Discogs["The Future#1"] = new("Nobot", "The Future", [], false,
            new DiscogsMatchDto(1, "The Future", "Nobot", ["Label A"], "2024", null, true, "The Future", ["https://labela.bandcamp.com/album/the-future"]));
        fake.Releases["https://labela.bandcamp.com/album/the-future"] = FakeLookup.Release(
            "https://labela.bandcamp.com/album/the-future", "The Future", "Label A", (99, "The Future", "Nobot"));

        var resolver = new CrateResolver(fake);
        var item = Item("Nobot", "The Future");
        var r = await resolver.ResolveAsync(item, _ => { }, default);
        Assert.Equal(CrateStatus.Ambiguous, r.Status);
        Assert.Equal(2, r.Candidates!.Count);

        var picked = await resolver.PickAsync(item, r.Candidates[0], _ => { }, default);
        Assert.Equal(CrateStatus.Found, picked.Status);
        Assert.Equal(99, picked.TrackId);
        Assert.Equal("picked", picked.Via);
    }

    [Fact]
    public async Task A_pasted_album_url_picks_the_track_by_title_or_asks_which_one()
    {
        var fake = new FakeLookup();
        const string album = "https://muiminasounds.bandcamp.com/album/abyss";
        fake.Releases[album] = FakeLookup.Release(album, "Abyss", "Muimina Sounds", (1, "Wail", "ITSA"), (5, "Ululation", "Into The Screaming Abyss"));
        var resolver = new CrateResolver(fake);

        var r = await resolver.FromUrlAsync(Item("Into The Screaming Abyss", "Ululation"), album, _ => { }, default);
        Assert.Equal((CrateStatus.Found, 5L, "pasted"), (r.Status, r.TrackId!.Value, r.Via));

        var other = await resolver.FromUrlAsync(Item("Someone", "Not There"), album, _ => { }, default);
        Assert.Equal(CrateStatus.Ambiguous, other.Status);
        Assert.Equal(2, other.Candidates!.Count);
        Assert.All(other.Candidates, c => Assert.Equal("bandcamp", c.Kind));
    }

    [Fact]
    public async Task SoundCloud_is_asked_only_on_demand_and_a_pasted_soundcloud_url_is_found_there()
    {
        var fake = new FakeLookup();
        fake.SoundCloud["Shesharka"] = new SoundCloudTrackDto("https://soundcloud.com/atriohm/shesharka", "Shesharka", "Atriohm", 123);
        var resolver = new CrateResolver(fake);
        var item = Item("Atriohm", "Shesharka", "Atriohm");

        await resolver.ResolveAsync(item, _ => { }, default);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("soundcloud"));

        var sc = await resolver.SoundCloudAsync(item, default);
        Assert.Equal(("soundcloud", "https://soundcloud.com/atriohm/shesharka"), (sc!.Source, sc.PageUrl));

        fake.SoundCloud["https://soundcloud.com/x/y"] = new SoundCloudTrackDto("https://soundcloud.com/x/y", "Y", "X", 1);
        var pasted = await resolver.FromUrlAsync(item, "https://soundcloud.com/x/y", _ => { }, default);
        Assert.Equal(("soundcloud", "pasted"), (pasted.Source, pasted.Via));
    }

    [Fact]
    public async Task A_bandcamp_buy_link_on_the_soundcloud_page_is_preferred_so_it_plays_in_the_bar()
    {
        var fake = new FakeLookup();
        const string album = "https://beatspace-parvati.bandcamp.com/album/uncharted-paths";
        fake.SoundCloud["Something Different"] = new SoundCloudTrackDto("https://soundcloud.com/codex-spiralis/something-different",
            "Something Different", "Codex Spiralis", 1853600883, [album]);
        fake.Releases[album] = FakeLookup.Release(album, "Uncharted Paths", "Parvati Records", (77, "Something Different", "Codex Spiralis"));

        var r = await new CrateResolver(fake).SoundCloudAsync(Item("Codex Spiralis", "Something Different", "Codex Spiralis"), default);
        Assert.Equal(("bandcamp", 77L, "soundcloud-link"), (r!.Source, r.TrackId!.Value, r.Via));
    }

    [Fact]
    public async Task A_spent_limit_stops_instead_of_marking_tracks_not_found()
    {
        var fake = new FakeLookup { DiscogsStatus = 429 };
        await Assert.ThrowsAsync<CrateLimitException>(() => new CrateResolver(fake).ResolveAsync(Item("A", "B"), _ => { }, default));
    }

    [Fact]
    public async Task A_truncated_title_matches_the_start_of_the_real_one()
    {
        var fake = new FakeLookup();
        fake.Lists["https://noizebug.bandcamp.com"] = [new("https://noizebug.bandcamp.com/album/the-cosmos-welcomes-you", "album", 1, "The Cosmos Welcomes You", "NoizeBug")];
        fake.Releases["https://noizebug.bandcamp.com/album/the-cosmos-welcomes-you"] = FakeLookup.Release(
            "https://noizebug.bandcamp.com/album/the-cosmos-welcomes-you", "The Cosmos Welcomes You", "NoizeBug",
            (3, "The Cosmos Welcomes You (Psilocybin Intelligence Remix)", "NoizeBug"));
        var r = await new CrateResolver(fake).ResolveAsync(
            Item("NoizeBug", "The Cosmos Welcomes You (Psilocybin Intellig… truncated)", "NoizeBug"), _ => { }, default);
        Assert.Equal(CrateStatus.Found, r.Status);
        Assert.Equal(3, r.TrackId);
    }
}
