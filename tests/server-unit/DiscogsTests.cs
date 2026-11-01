using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using TraktorGoogleDrive.Server.Discover;

namespace TraktorGoogleDrive.Tests;

/// <summary>The Discogs adapter on recorded answers (ADR 20261025-54, "Crates"): no test calls Discogs.</summary>
public class DiscogsParserTests
{
    [Fact]
    public void Search_results_split_the_artist_from_the_release_title()
    {
        var results = DiscogsParser.ParseSearch(Fixtures.Discogs("search-tengri-patch-hell.json"));
        var first = results[0];
        Assert.Equal(29681248, first.Id);
        Assert.Equal("Tengri", first.Artist);
        Assert.Equal("Testimony 20", first.Title);
        Assert.Equal("2024", first.Year);
        Assert.Equal(["Parvati Records"], first.Labels);
        Assert.Contains("File", first.Formats);
        Assert.Empty(DiscogsParser.ParseSearch(Fixtures.Discogs("search-empty.json")));
    }

    [Fact]
    public void A_release_gives_its_tracklist_labels_and_date()
    {
        var release = DiscogsParser.ParseRelease(Fixtures.Discogs("release-29681248-testimony-20.json"))!;
        Assert.Equal("Testimony 20", release.Title);
        Assert.Equal("2024-02-02", release.Released);
        Assert.Equal(10916, Assert.Single(release.Labels).Id);
        Assert.Contains(release.Tracklist, t => t.Title == "Patch Hell");
        Assert.Equal("https://www.discogs.com/release/29681248-Tengri-Testimony-20", release.Uri);
    }

    [Fact]
    public void A_label_profile_gives_its_bandcamp_page_canonically()
    {
        var label = DiscogsParser.ParseProfile(Fixtures.Discogs("label-10916-parvati.json"))!;
        Assert.Equal("Parvati Records", label.Name);
        Assert.Equal(["https://beatspace-parvati.bandcamp.com/music"], DiscogsParser.BandcampPages(label.Urls));
    }

    [Theory]
    [InlineData("Purist (3) & Piazer*", new[] { "purist", "piazer" })]
    [InlineData("Raveen ft. Mafia", new[] { "raveen", "mafia" })]
    [InlineData("Critter & Xolotl & Gadikt", new[] { "critter", "xolotl", "gadikt" })]
    public void Artist_keys_drop_discogs_numbering_and_split_credits(string credit, string[] keys) =>
        Assert.Equal(keys.ToHashSet(), DiscogsNames.ArtistKeys(credit));

    [Theory]
    [InlineData("The Hierophants (Via Axis, Purist & Yachay)", "The Hierophants")]
    [InlineData("Raveen ft. Mafia", "Raveen")]
    [InlineData("Tengri & Jahbo", "Tengri")]
    public void The_search_uses_the_first_artist(string artist, string first) =>
        Assert.Equal(first, DiscogsSource.FirstArtist(artist));

    [Fact]
    public void Truncated_titles_match_the_start_and_search_without_the_mark()
    {
        Assert.Equal("Self R", DiscogsNames.SearchTitle("Self R… (truncated)"));
        Assert.True(DiscogsNames.TitleMatches("Self R… (truncated)", "Self Reflection", truncated: true));
        Assert.False(DiscogsNames.TitleMatches("Self R… (truncated)", "Self Reflection", truncated: false));
        Assert.True(DiscogsNames.TitleMatches("Wagwan (Original Mix)", "Wagwan", truncated: false));
    }
}

public class DiscogsRankingTests
{
    private static DiscogsSearchResult R(long id, string artist, string title, string year = "2024", params string[] formats) =>
        new(id, artist, title, year, ["L"], null, formats.Length == 0 ? ["File"] : formats, $"/release/{id}");

    [Fact]
    public void Every_artist_credited_beats_a_compilation_and_a_stranger_is_dropped()
    {
        var ranked = DiscogsRanking.Rank([
            R(1, "Various", "Forest Sounds Vol. 2"),
            R(2, "Tengri & Jahbo", "Patch Hell"),
            R(3, "Someone Else", "Patch Hell"),
            R(4, "Tengri", "Testimony 20"),
        ], "Tengri & Jahbo", "Patch Hell");

        Assert.Equal([2L, 4L, 1L], ranked.Select(c => c.Id));
        Assert.Equal(6, ranked[0].Score);   // all artists 4, single named after the track 1, digital 1
        Assert.Equal("https://www.discogs.com/release/2", ranked[0].WebUrl);
        Assert.False(DiscogsRanking.IsAmbiguous(ranked));
    }

    [Fact]
    public void The_recorded_search_ranks_the_album_with_the_track()
    {
        var ranked = DiscogsRanking.Rank(DiscogsParser.ParseSearch(Fixtures.Discogs("search-purist-clustered-souls.json")),
            "Purist & Piazer", "Clustered Souls");
        var best = ranked[0];
        Assert.Equal("Purist (3)", best.Artist);
        Assert.Equal("Paradox Manipulation", best.Title);
    }

    [Fact]
    public void Two_artists_sharing_a_name_are_ambiguous_but_an_ep_and_a_compilation_are_not()
    {
        var sameName = DiscogsRanking.Rank([R(1, "Nobot", "The Future EP"), R(2, "Nobot (2)", "Futures")], "Nobot", "The Future");
        Assert.True(DiscogsRanking.IsAmbiguous(sameName));

        var epAndVa = DiscogsRanking.Rank([R(1, "Gidra", "Enter The Grid"), R(2, "Various", "Grid Comp", "2025")], "Gidra", "Enter The Grid");
        Assert.False(DiscogsRanking.IsAmbiguous(epAndVa));

        var sameArtistTwoReleases = DiscogsRanking.Rank([R(1, "Tengri", "A"), R(2, "Tengri", "B")], "Tengri", "X");
        Assert.False(DiscogsRanking.IsAmbiguous(sameArtistTwoReleases));

        // A collaboration found on each artist's own album: both fine, nothing to ask.
        var eachArtist = DiscogsRanking.Rank([R(1, "Tengri", "A"), R(2, "Jahbo", "B")], "Tengri & Jahbo", "X");
        Assert.False(DiscogsRanking.IsAmbiguous(eachArtist));
    }
}

public class DiscogsSourceTests
{
    private sealed class Recorded(Dictionary<string, string> pages, List<string> log) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            log.Add(url);
            return Task.FromResult(pages.TryGetValue(url, out var file)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Discogs(file)) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (DiscogsSource Source, List<string> Log) Source()
    {
        var log = new List<string>();
        var pages = new Dictionary<string, string>
        {
            [DiscogsSource.SearchUrl("Tengri", "Patch Hell").ToString()] = "search-tengri-patch-hell.json",
            [DiscogsSource.ReleaseUrl(29681248).ToString()] = "release-29681248-testimony-20.json",
            [DiscogsSource.LabelUrl(10916).ToString()] = "label-10916-parvati.json",
            [DiscogsSource.SearchUrl("Ianuaria", "Bioport").ToString()] = "search-empty.json",
        };
        var fetcher = new PoliteFetcher(new HttpClient(new Recorded(pages, log)), TimeProvider.System, NullLogger.Instance,
            TimeSpan.Zero, 1000, (_, _) => Task.CompletedTask, accept: "application/json");
        return (new DiscogsSource(fetcher, TimeProvider.System), log);
    }

    [Fact]
    public async Task A_lookup_reads_search_release_and_label_and_finds_the_bandcamp_page()
    {
        var (discogs, log) = Source();
        var lookup = await discogs.LookupAsync("Tengri & Jahbo", "Patch Hell", null, default);

        var best = lookup.Best!;
        Assert.Equal("Testimony 20", best.Title);
        Assert.True(best.TrackFound);
        Assert.Equal(["Parvati Records"], best.Labels);
        Assert.Equal("2024-02-02", best.Released);
        Assert.Equal(["https://beatspace-parvati.bandcamp.com/music"], best.BandcampUrls);
        Assert.Equal(3, log.Count);   // search, release, label: the label had a Bandcamp page, so no artist profile

        // Cached: the same lookup costs nothing.
        await discogs.LookupAsync("Tengri & Jahbo", "Patch Hell", null, default);
        Assert.Equal(3, log.Count);
        Assert.All(log, u => Assert.StartsWith("https://api.discogs.com/", u));
    }

    [Fact]
    public async Task Nothing_on_discogs_is_one_request_and_no_best()
    {
        var (discogs, log) = Source();
        var lookup = await discogs.LookupAsync("Ianuaria & Muscaria", "Bioport", null, default);
        Assert.Null(lookup.Best);
        Assert.Empty(lookup.Candidates);
        Assert.Single(log);
    }
}

public class SoundCloudTests
{
    [Theory]
    [InlineData("https://soundcloud.com/codex-spiralis/something-different", "https://soundcloud.com/codex-spiralis/something-different")]
    [InlineData("soundcloud.com/Gidra/spider-skunk-edit-4?in=x", "https://soundcloud.com/gidra/spider-skunk-edit-4")]
    [InlineData("https://m.soundcloud.com/gidra/viral-virus", "https://soundcloud.com/gidra/viral-virus")]
    [InlineData("https://soundcloud.com/gidra/sets/an-album", null)]
    [InlineData("https://soundcloud.com/search/sounds", null)]
    [InlineData("https://soundcloud.com/gidra", null)]
    [InlineData("https://evil.example/gidra/x", null)]
    [InlineData("https://on.soundcloud.com/abc", null)]
    public void Only_track_pages_are_accepted(string input, string? expected) =>
        Assert.Equal(expected, SoundCloudUrls.TrackPage(input));

    [Fact]
    public void Guesses_are_the_uploaders_then_the_artist_with_the_title_slug()
    {
        Assert.Equal(
            ["https://soundcloud.com/tengri/patch-hell", "https://soundcloud.com/jahbo/patch-hell"],
            SoundCloudUrls.Guesses("Tengri & Jahbo", "Patch Hell", "Tengri, Jahbo"));
        Assert.Equal(["https://soundcloud.com/faeyan/le-souffle-datlas"], SoundCloudUrls.Guesses("Faeyan", "Le Souffle d'Atlas", "Faeyan"));
        Assert.Empty(SoundCloudUrls.Guesses("Seraburayka", "Self R… (truncated)", "Syros"));
    }

    [Fact]
    public void An_oembed_answer_gives_the_track_its_title_and_the_buy_link_in_its_description()
    {
        var track = SoundCloudSource.ParseOEmbed(Fixtures.SoundCloud("oembed-codex-spiralis-something-different.json"),
            "https://soundcloud.com/codex-spiralis/something-different")!;
        Assert.Equal("Something Different", track.Title);
        Assert.Equal("Codex Spiralis", track.Author);
        Assert.Equal(1853600883, track.TrackId);
        Assert.Equal(["https://beatspace-parvati.bandcamp.com/album/uncharted-paths"], track.BandcampUrls);
    }
}
