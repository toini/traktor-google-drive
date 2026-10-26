using TraktorGoogleDrive.Server.Discover;

namespace TraktorGoogleDrive.Tests;

/// <summary>Against pages recorded in spike S1 (ADR 20261025-54), trimmed of fan data.</summary>
public class BandcampParserTests
{
    [Fact]
    public void Music_page_with_every_release_rendered()
    {
        var items = BandcampParser.ParseMusicPage(
            Fixtures.Bandcamp("carnivorrecords-music.html"), new Uri("https://carnivorrecords.bandcamp.com/music"));

        Assert.Equal(10, items.Count);
        Assert.Equal("https://carnivorrecords.bandcamp.com/album/feeding-generation-z-ii", items[0].Url);
        var internalMotion = Assert.Single(items, i => i.Url.EndsWith("/album/internal-motion"));
        Assert.Equal("album", internalMotion.Type);
        Assert.Equal("Internal Motion", internalMotion.Title);

        // A single track in the grid, with its artist in the override span.
        var single = Assert.Single(items, i => i.Type == "track");
        Assert.Equal("https://carnivorrecords.bandcamp.com/track/5am-original-mix", single.Url);
        Assert.Equal("5AM (Original Mix)", single.Title);
        Assert.Equal("DSSLV & T Y", single.Artist);
    }

    [Fact]
    public void Music_page_reads_the_lazy_loaded_releases_too()
    {
        var items = BandcampParser.ParseMusicPage(
            Fixtures.Bandcamp("darkersounds-music.html"), new Uri("https://darkersounds.bandcamp.com/music"));

        // 16 rendered (12 albums, 4 singles) + 43 in data-client-items (spike S1).
        Assert.Equal(59, items.Count);
        Assert.Equal(4, items.Take(16).Count(i => i.Type == "track"));
        Assert.Equal("https://darkersounds.bandcamp.com/album/shadow-alliance-ep", items[0].Url);
        Assert.Equal(58158226, items[0].Id);
        var lazy = Assert.Single(items, i => i.Url.EndsWith("/album/ds043-purgatory-ep"));
        Assert.Equal("Algia", lazy.Artist);
        Assert.Equal(1122052358, lazy.Id);
        Assert.Equal(items.Count, items.Select(i => i.Url).Distinct().Count());
    }

    [Fact]
    public void Album_page()
    {
        var r = BandcampParser.ParseRelease(
            Fixtures.Bandcamp("darkersounds-album-shadow-alliance-ep.html"),
            new Uri("https://darkersounds.bandcamp.com/album/shadow-alliance-ep"))!;

        Assert.Equal("album", r.Type);
        Assert.Equal(58158226, r.Id);
        Assert.Equal("Shadow Alliance EP", r.Title);
        Assert.Equal("Hefty", r.Artist);
        Assert.Equal("Darker Sounds", r.Label);
        Assert.Equal("https://darkersounds.bandcamp.com", r.LabelUrl);
        Assert.Equal(new DateTimeOffset(2025, 11, 9, 18, 21, 12, TimeSpan.Zero), r.ReleaseDate);
        Assert.Contains("Hard Techno", r.Tags);
        Assert.Equal(3, r.StreamingLimit);
        Assert.Null(r.AlbumUrl);

        Assert.Equal(5, r.Tracks.Count);
        var first = r.Tracks[0];
        Assert.Equal(2927639579, first.TrackId);
        Assert.Equal(1, first.Number);
        Assert.Equal("Joint Listening Post", first.Title);
        Assert.Equal("Hefty", first.Artist);
        Assert.Equal("https://darkersounds.bandcamp.com/track/joint-listening-post", first.TrackUrl);
        Assert.True(BandcampUrls.IsStreamUrl(first.StreamUrl));
        // token=<unix expiry>_<sig>: fetch time + 24 h.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793013747), first.StreamExpires);
    }

    [Fact]
    public void Track_page_maps_to_its_release()
    {
        var r = BandcampParser.ParseRelease(
            Fixtures.Bandcamp("darkersounds-track-joint-listening-post.html"),
            new Uri("https://darkersounds.bandcamp.com/track/joint-listening-post"))!;

        Assert.Equal("track", r.Type);
        Assert.Equal("https://darkersounds.bandcamp.com/album/shadow-alliance-ep", r.AlbumUrl);
        Assert.Equal(new DateTimeOffset(2025, 11, 9, 18, 21, 12, TimeSpan.Zero), r.ReleaseDate);
        var t = Assert.Single(r.Tracks);
        Assert.Equal("Joint Listening Post", t.Title);
        Assert.Equal("https://darkersounds.bandcamp.com/track/joint-listening-post", t.TrackUrl);
    }

    [Fact]
    public void Numbered_artist_dash_title_is_split_into_artist_and_title()
    {
        var r = BandcampParser.ParseRelease(
            Fixtures.Bandcamp("carnivorrecords-album-internal-motion.html"),
            new Uri("https://carnivorrecords.bandcamp.com/album/internal-motion"))!;

        Assert.Equal("Carnivor Records", r.Label);
        Assert.Equal(new DateTimeOffset(2020, 8, 19, 0, 0, 0, TimeSpan.Zero), r.ReleaseDate);
        // "01. obwigszyh - 1616 (Original Mix)" with no track artist.
        Assert.Equal("1616 (Original Mix)", r.Tracks[0].Title);
        Assert.Equal("obwigszyh", r.Tracks[0].Artist);
        Assert.Equal(["1616 (Original Mix)", "Internal Motion (Original Mix)", "Burnt Side (Original Mix)", "Track 4 (Original Mix)"],
            r.Tracks.Select(t => t.Title));
    }

    [Theory]
    [InlineData("1616", null, "Release Artist", "1616", "Release Artist")]
    [InlineData("01. A - B", null, "X", "B", "A")]
    [InlineData("A - B (Remix)", null, "X", "B (Remix)", "A")]
    [InlineData("Title", "Own Artist", "X", "Title", "Own Artist")]
    public void Split_title(string raw, string? trackArtist, string releaseArtist, string title, string artist)
    {
        Assert.Equal((title, artist), BandcampParser.SplitTitle(raw, trackArtist, releaseArtist));
    }

    [Fact]
    public void A_page_without_release_data_is_null_not_an_exception()
    {
        Assert.Null(BandcampParser.ParseRelease("<html><body>Not found</body></html>", new Uri("https://x.bandcamp.com/album/y")));
        Assert.Null(BandcampParser.ParseRelease("<div data-tralbum=\"{broken\"></div>", new Uri("https://x.bandcamp.com/album/y")));
        Assert.Empty(BandcampParser.ParseMusicPage("<html></html>", new Uri("https://x.bandcamp.com/music")));
    }
}
