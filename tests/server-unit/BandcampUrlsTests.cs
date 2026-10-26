using TraktorGoogleDrive.Server.Discover;

namespace TraktorGoogleDrive.Tests;

/// <summary>The server fetches only what these allow (ADR 20261025-54 §9: no SSRF, no /search, no /api/).</summary>
public class BandcampUrlsTests
{
    [Theory]
    [InlineData("https://carnivorrecords.bandcamp.com", "https://carnivorrecords.bandcamp.com/music")]
    [InlineData("http://carnivorrecords.bandcamp.com/", "https://carnivorrecords.bandcamp.com/music")]
    [InlineData("https://CarnivorRecords.bandcamp.com/album/internal-motion?x=1", "https://carnivorrecords.bandcamp.com/music")]
    [InlineData("carnivorrecords.bandcamp.com/music", "https://carnivorrecords.bandcamp.com/music")]
    [InlineData("carnivorrecords", "https://carnivorrecords.bandcamp.com/music")]
    public void Music_page(string input, string expected) =>
        Assert.Equal(expected, BandcampUrls.MusicPage(input)?.ToString());

    [Theory]
    [InlineData("https://bandcamp.com/search?q=x")]
    [InlineData("https://bandcamp.com/toini")]
    [InlineData("https://www.bandcamp.com/")]
    [InlineData("https://evil.example.com/x.bandcamp.com")]
    [InlineData("https://x.bandcamp.com.evil.example/")]
    [InlineData("https://x.bandcamp.com:8443/")]
    [InlineData("https://user@x.bandcamp.com/")]
    [InlineData("ftp://x.bandcamp.com/")]
    [InlineData("https://t4.bandcamp.com/")]
    [InlineData("")]
    [InlineData(null)]
    public void Not_a_music_page(string? input) => Assert.Null(BandcampUrls.MusicPage(input));

    [Theory]
    [InlineData("https://darkersounds.bandcamp.com/album/shadow-alliance-ep", "https://darkersounds.bandcamp.com/album/shadow-alliance-ep")]
    [InlineData("darkersounds.bandcamp.com/track/joint-listening-post/?from=x#t", "https://darkersounds.bandcamp.com/track/joint-listening-post")]
    public void Release_page(string input, string expected) =>
        Assert.Equal(expected, BandcampUrls.ReleasePage(input)?.ToString());

    [Theory]
    [InlineData("https://darkersounds.bandcamp.com/music")]
    [InlineData("https://darkersounds.bandcamp.com/api/x/album/y")]
    [InlineData("https://darkersounds.bandcamp.com/album/../api")]
    [InlineData("https://bandcamp.com/album/x")]
    [InlineData("https://example.com/album/x")]
    public void Not_a_release_page(string input) => Assert.Null(BandcampUrls.ReleasePage(input));

    [Theory]
    [InlineData("https://t4.bcbits.com/stream/abc/mp3-128/1?p=0", true)]
    [InlineData("http://t4.bcbits.com/stream/abc", false)]
    [InlineData("https://bcbits.com.evil.example/stream", false)]
    [InlineData("https://example.com/", false)]
    public void Stream_urls(string url, bool ok) => Assert.Equal(ok, BandcampUrls.IsStreamUrl(url));
}
