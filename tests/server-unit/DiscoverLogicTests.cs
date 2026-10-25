using TraktorGoogleDrive.Discover;
using TraktorGoogleDrive.Services;

namespace TraktorGoogleDrive.Tests;

public class NamesTests
{
    [Theory]
    [InlineData("Obwigszyh", "obwigszyh")]
    [InlineData("DSSLV & T Y", "dsslv and t y")]
    [InlineData("Café  Noir!", "cafe noir")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, Names.Normalize(input));

    [Fact]
    public void Split_artists_and_drop_the_stop_list()
    {
        Assert.Equal(["Kristonov", "Strasse Killer"], Names.SplitArtists("Kristonov & Strasse Killer"));
        Assert.Equal(["BIJOU", "Tony Watts"], Names.SplitArtists("BIJOU feat. Tony Watts"));
        Assert.Empty(Names.SplitArtists("Various Artists"));
        Assert.Empty(Names.SplitArtists("NN"));
        Assert.Empty(Names.SplitArtists("=============="));
        Assert.True(Names.IsStopName("Salceanu\n\n Share / Embed  You own this"));
        Assert.True(Names.IsStopName("DistroKid"));
        Assert.False(Names.IsStopName("Hefty"));
    }

    [Theory]
    [InlineData("1616 (Original Mix)", "1616")]
    [InlineData("1616", "1616")]
    [InlineData("Rockstar (feat. Tony Watts) (BADVOID_Remix)", "rockstar badvoid remix")]
    [InlineData("Burnt Side - Original Mix", "burnt side")]
    public void Title_key(string title, string expected) => Assert.Equal(expected, Names.TitleKey(title));
}

public class SeedFinderTests
{
    private static CollectionTrack T(string artist, string title, string? label, int stars, string genre,
        int plays = 0, string? comment = null, string? imported = "2025/12/13") =>
        new(artist, title, label, stars, plays, Models.TrackDate.Parse(imported), null, comment, [genre]);

    [Theory]
    [InlineData("KOVA LOPPU. eti lisaa", true)]
    [InlineData("3-1 Hullu. Eti lisää", true)]
    [InlineData("2-2 ETI LISÄÄ. pois 4. looppaa 4 kerran", true)]
    [InlineData("etsi lisää tältä labelilta", true)]
    [InlineData("1-3 lisää luuppi!", false)]
    [InlineData("leikattu", false)]
    [InlineData(null, false)]
    public void Eti_lisaa_marker(string? comment, bool expected) => Assert.Equal(expected, SeedFinder.SaysEtiLisaa(comment));

    [Fact]
    public void Eti_lisaa_seeds_the_artist_and_the_label_in_the_tracks_genre()
    {
        var tracks = new[]
        {
            T("Obwigszyh", "1616 (Original Mix)", "Carnivor Records", 5, "H", comment: "KOVA LOPPU. eti lisaa"),
            T("Dragon", "Tetrakis", "2TO6 Records", 5, "P", comment: "1-3 lisää luuppi!"),
            T("Someone", "Else", "Other", 5, "H"),
        };

        var seeds = SeedFinder.EtiLisaa(tracks);

        Assert.Equal(2, seeds.Count);
        Assert.Contains(seeds, s => s is { Kind: SeedKind.Artist, Name: "Obwigszyh", Genre: "H", Origin: SeedOrigin.EtiLisaa });
        Assert.Contains(seeds, s => s is { Kind: SeedKind.Label, Name: "Carnivor Records", Genre: "H" });
        Assert.All(seeds, s => Assert.Contains("eti lisaa", s.Reason));
    }

    [Fact]
    public void Playlist_seed_counts_well_rated_tracks_once_each()
    {
        var a = T("Hefty", "One", "Darker Sounds", 5, "H");
        var b = T("Hefty", "Two", "Darker Sounds", 4, "H");
        var c = T("Noseda", "Three", "Erebos Records", 4, "H");
        var d = T("Various Artists", "Four", "RAW.", 4, "H");
        var low = T("Low", "Five", "Fresh Cut", 3, "H");
        var playlist = new[] { a, b, c, d, low, a };  // a appears twice, like H16's duplicates

        var seeds = SeedFinder.FromPlaylist(playlist, "H", "H16 2023/2025 Darker Sounds-2");

        // Most tracks first, labels before artists, then by name.
        Assert.Equal(["Darker Sounds", "Hefty", "Erebos Records", "RAW.", "Noseda"], seeds.Select(s => s.Name));
        Assert.Equal(2, seeds.Single(s => s.Name == "Hefty").Tracks);
        Assert.DoesNotContain(seeds, s => s.Name == "Various Artists" || s.Name == "Low" || s.Name == "Fresh Cut");
        Assert.All(seeds, s => Assert.Equal(SeedOrigin.Playlist, s.Origin));

        Assert.Equal(["Darker Sounds", "Erebos Records", "Fresh Cut", "RAW."],
            SeedFinder.FromPlaylist(playlist, "H", "x", minStars: 3, artists: false).Select(s => s.Name).Order());
    }

    [Fact]
    public void Top_list_needs_two_tracks_or_one_good_one_and_ranks_recent_rated_plays()
    {
        var today = new DateOnly(2026, 10, 25);
        var tracks = new[]
        {
            T("A", "1", "Industrial Philharmonics", 3, "H", plays: 100, imported: "2017/1/1"),
            T("B", "2", "Industrial Philharmonics", 3, "H", plays: 100, imported: "2017/1/1"),
            T("C", "3", "Darker Sounds", 5, "H", plays: 10, imported: "2025/12/13"),
            T("D", "4", "One Off", 2, "H"),
            T("E", "5", "Psy Label", 5, "P"),
        };

        var labels = SeedFinder.TopList(tracks, "H", SeedKind.Label, today);

        Assert.Equal(["Industrial Philharmonics", "Darker Sounds"], labels.Select(l => l.Name));
        Assert.Equal(10.0, labels[0].Score);              // 200 plays / 20, no 4★, 9 years old
        Assert.Equal(3 + 0.5 + 5, labels[1].Score);       // one 4★, 10 plays, imported within the year
    }

    [Theory]
    [InlineData("2026-01-01", 5)]
    [InlineData("2021-10-25", 0)]
    [InlineData(null, 0)]
    public void Recency(string? last, double expected) =>
        Assert.Equal(expected, SeedFinder.Recency(last is null ? null : DateOnly.Parse(last), new DateOnly(2026, 10, 25)), 2);
}

public class DiscoverStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static DiscoverEvent Seed(string key = "label:carnivor records:H", int minutes = 0, SeedOrigin origin = SeedOrigin.TopList) => new()
    {
        Type = "seed-add", At = T0.AddMinutes(minutes), SeedKey = key, Kind = SeedKind.Label,
        Name = "Carnivor Records", Genre = "H", Origin = origin, Url = "https://carnivorrecords.bandcamp.com",
    };

    private static DiscoverEvent Mark(string month, int minutes, bool clear = false) => new()
    {
        Type = "watermark", At = T0.AddMinutes(minutes), SeedKey = "label:carnivor records:H", Month = month, Clear = clear ? true : null,
    };

    [Fact]
    public void Watermarks_only_move_forward_unless_set_by_hand()
    {
        var s = DiscoverState.Fold([Seed(), Mark("2023-06", 1), Mark("2022-01", 2), Mark("2023-04", 3)]);
        Assert.Equal("2023-06", s.Seeds.Single().Value.Watermark);

        s = DiscoverState.Fold([Seed(), Mark("2023-06", 1), Mark("2021-05", 2, clear: true)]);
        Assert.Equal("2021-05", s.Seeds.Single().Value.Watermark);
    }

    [Fact]
    public void Events_from_two_devices_merge_by_time_and_the_later_verdict_wins()
    {
        var tablet = new DiscoverEvent { Type = "track", At = T0.AddMinutes(5), Device = "tablet", TrackKey = "bandcamp:1", Pass1 = Pass1.Like, Rating = 4 };
        var laptop = new DiscoverEvent { Type = "track", At = T0.AddMinutes(1), Device = "laptop", TrackKey = "bandcamp:1", Pass1 = Pass1.Skip, Note = "too slow" };

        var s = DiscoverState.Fold([tablet, laptop]);  // order of arrival does not matter

        var t = s.Tracks["bandcamp:1"];
        Assert.Equal(Pass1.Like, t.Pass1);
        Assert.Equal(4, t.Rating);
        Assert.Equal("too slow", t.Note);
        Assert.True(t.Heard);
    }

    [Fact]
    public void Clearing_a_verdict_keeps_the_track_heard()
    {
        var s = DiscoverState.Fold([
            new DiscoverEvent { Type = "track", At = T0, TrackKey = "bandcamp:1", Pass1 = Pass1.Like, Decision = Decision.Buy, Rating = 5 },
            new DiscoverEvent { Type = "track", At = T0.AddMinutes(1), TrackKey = "bandcamp:1", Clear = true },
        ]);
        var t = s.Tracks["bandcamp:1"];
        Assert.Null(t.Pass1);
        Assert.Null(t.Decision);
        Assert.Null(t.Rating);
        Assert.True(t.Heard);
    }

    [Fact]
    public void Re_adding_a_seed_keeps_the_stronger_origin()
    {
        var s = DiscoverState.Fold([Seed(origin: SeedOrigin.TopList), Seed(minutes: 1, origin: SeedOrigin.EtiLisaa)]);
        Assert.Equal(SeedOrigin.EtiLisaa, s.Seeds.Single().Value.Origin);
    }

    [Fact]
    public void Jsonl_round_trip_skips_bad_lines()
    {
        var events = new[] { Seed(), Mark("2024-02", 1) };
        var text = DiscoverStore.Serialize(events) + "{not json\n";
        var back = DiscoverStore.Parse(text);
        Assert.Equal(2, back.Count);
        Assert.Equal("2024-02", DiscoverState.Fold(back).Seeds.Single().Value.Watermark);
        Assert.Contains("\"t\":\"seed-add\"", text);
    }
}

public class QueueBuilderTests
{
    private const string SeedKey = "label:carnivor records:H";

    private static DiscoverState State(string? watermark = null)
    {
        var events = new List<DiscoverEvent>
        {
            new() { Type = "seed-add", At = DateTimeOffset.UnixEpoch, SeedKey = SeedKey, Kind = SeedKind.Label, Name = "Carnivor Records", Genre = "H" },
        };
        if (watermark is not null)
            events.Add(new() { Type = "watermark", At = DateTimeOffset.UnixEpoch.AddSeconds(1), SeedKey = SeedKey, Month = watermark });
        return DiscoverState.Fold(events);
    }

    private static ReleaseInfo Release(string slug, string date, params (long Id, string Artist, string Title)[] tracks) => new(
        "bandcamp", $"https://carnivorrecords.bandcamp.com/album/{slug}", slug, "Various", "Carnivor Records", DateOnly.Parse(date), [],
        tracks.Select((t, i) => new ReleaseTrack(t.Id, i + 1, t.Title, t.Artist, 300, null, true)).ToList(), SeedKey);

    private static readonly ReleaseInfo InternalMotion = Release("internal-motion", "2020-08-19",
        (1402866115, "obwigszyh", "1616 (Original Mix)"), (776289323, "obwigszyh", "Internal Motion (Original Mix)"));
    private static readonly ReleaseInfo Newer = Release("newer", "2024-03-01", (1, "X", "A"), (2, "X", "B"));
    private static readonly ReleaseInfo Older = Release("older", "2019-05-01", (3, "Y", "C"));

    private static readonly OwnedIndex Owned = new([("Obwigszyh", "1616 (Original Mix)")]);

    [Fact]
    public void Oldest_release_first_owned_tracks_never_appear()
    {
        var q = QueueBuilder.Build(State(), "H", [Newer, InternalMotion, Older], Owned);

        Assert.Equal(["bandcamp:3", "bandcamp:776289323", "bandcamp:1", "bandcamp:2"], q.Select(i => i.TrackKey));
    }

    [Fact]
    public void Releases_on_or_before_the_watermark_are_never_queued()
    {
        var q = QueueBuilder.Build(State("2020-08"), "H", [Newer, InternalMotion, Older], Owned);
        Assert.Equal(["bandcamp:1", "bandcamp:2"], q.Select(i => i.TrackKey));
    }

    [Fact]
    public void Heard_tracks_and_heard_releases_never_come_back_whatever_the_verdict()
    {
        var state = State();
        state.Apply(new DiscoverEvent { Type = "track", At = DateTimeOffset.UnixEpoch, TrackKey = "bandcamp:1", Pass1 = Pass1.Like });
        state.Apply(new DiscoverEvent { Type = "track", At = DateTimeOffset.UnixEpoch, TrackKey = "bandcamp:2", Clear = true });
        state.Apply(new DiscoverEvent { Type = "release-heard", At = DateTimeOffset.UnixEpoch, ReleaseUrl = Older.Url });

        var q = QueueBuilder.Build(state, "H", [Newer, InternalMotion, Older], Owned);

        Assert.Equal(["bandcamp:776289323"], q.Select(i => i.TrackKey));
    }

    [Fact]
    public void Other_genres_seeds_stay_out()
    {
        Assert.Empty(QueueBuilder.Build(State(), "Z", [Newer], Owned));
    }

    [Fact]
    public void Heard_through_is_offered_up_to_the_first_month_with_something_left()
    {
        var state = State();
        var seed = state.Seeds[SeedKey];
        state.Apply(new DiscoverEvent { Type = "track", At = DateTimeOffset.UnixEpoch, TrackKey = "bandcamp:3", Pass1 = Pass1.Skip });
        state.Apply(new DiscoverEvent { Type = "track", At = DateTimeOffset.UnixEpoch, TrackKey = "bandcamp:776289323", Pass1 = Pass1.Like });

        // 2019-05 heard, 2020-08 heard (one track owned), 2024-03 untouched.
        Assert.Equal("2020-08", QueueBuilder.HeardThrough(state, seed, [Newer, InternalMotion, Older], Owned));
    }
}

public class ShortlistTests
{
    [Fact]
    public void Copy_links_groups_by_release_with_its_url_first()
    {
        var tracks = new[]
        {
            new TrackState { Key = "bandcamp:1", ReleaseUrl = "https://carnivorrecords.bandcamp.com/album/internal-motion", Label = "Carnivor Records",
                ReleaseTitle = "Internal Motion", ReleaseDate = new DateOnly(2020, 8, 19), Artist = "obwigszyh", Title = "Burnt Side (Original Mix)", Rating = 4, Note = "kova loppu" },
            new TrackState { Key = "bandcamp:2", ReleaseUrl = "https://carnivorrecords.bandcamp.com/album/internal-motion", Label = "Carnivor Records",
                ReleaseTitle = "Internal Motion", ReleaseDate = new DateOnly(2020, 8, 19), Artist = "obwigszyh", Title = "Track 4 (Original Mix)" },
        };

        Assert.Equal(
            "Carnivor Records — Internal Motion (2020-08-19)\n"
            + "https://carnivorrecords.bandcamp.com/album/internal-motion\n"
            + "  obwigszyh – Burnt Side (Original Mix) ★★★★ — kova loppu\n"
            + "  obwigszyh – Track 4 (Original Mix)\n",
            DiscoverService.ShortlistText(tracks));
    }
}
