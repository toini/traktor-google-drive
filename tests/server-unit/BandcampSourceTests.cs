using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using TraktorGoogleDrive.Server.Discover;

namespace TraktorGoogleDrive.Tests;

/// <summary>Politeness and caching (ADR 20261025-54 decision 1), with a fake store and a fake clock.</summary>
public class BandcampSourceTests
{
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeStore(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Page(string fixture) =>
        new(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Bandcamp(fixture)) };

    private static HttpResponseMessage ByPath(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/music" => Page("carnivorrecords-music.html"),
        "/album/internal-motion" => Page("carnivorrecords-album-internal-motion.html"),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    private static (BandcampSource Source, FakeStore Store, FakeClock Clock, List<TimeSpan> Waits) Make(
        int cap = 500, Func<HttpRequestMessage, HttpResponseMessage>? answer = null)
    {
        // The recorded stream URLs expire at 1793015596 (2026-10-26 11:53 UTC).
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeSeconds(1793015596).AddHours(-20));
        var store = new FakeStore(answer ?? ByPath);
        var waits = new List<TimeSpan>();
        var fetcher = new PoliteFetcher(new HttpClient(store), clock, NullLogger.Instance,
            TimeSpan.FromSeconds(2), cap, (wait, _) => { waits.Add(wait); clock.Now += wait; return Task.CompletedTask; });
        return (new BandcampSource(fetcher, clock), store, clock, waits);
    }

    private static readonly Uri Music = new("https://carnivorrecords.bandcamp.com/music");
    private static readonly Uri Release = new("https://carnivorrecords.bandcamp.com/album/internal-motion");

    [Fact]
    public async Task Requests_are_spaced_two_seconds_apart_and_carry_an_honest_user_agent()
    {
        var (source, store, _, waits) = Make();

        await source.ListReleasesAsync(Music, refresh: false, default);
        await source.GetReleaseAsync(Release, needStreams: false, default);

        Assert.Equal(2, store.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(2)], waits);
        Assert.All(store.Requests, r => Assert.Equal(PoliteFetcher.UserAgent, r.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task Concurrent_callers_queue_rather_than_burst()
    {
        var (source, store, _, waits) = Make();
        var urls = Enumerable.Range(0, 4).Select(i => new Uri($"https://label{i}.bandcamp.com/music"));

        await Task.WhenAll(urls.Select(u => source.ListReleasesAsync(u, false, default)));

        Assert.Equal(4, store.Requests.Count);
        Assert.Equal(3, waits.Count);
    }

    [Fact]
    public async Task A_second_look_within_the_day_is_served_from_cache()
    {
        var (source, store, clock, _) = Make();

        var first = await source.ListReleasesAsync(Music, false, default);
        clock.Now += TimeSpan.FromHours(23);
        var second = await source.ListReleasesAsync(Music, false, default);
        Assert.Same(first, second);
        Assert.Single(store.Requests);

        await source.ListReleasesAsync(Music, refresh: true, default);
        Assert.Equal(2, store.Requests.Count);

        clock.Now += TimeSpan.FromHours(25);
        await source.ListReleasesAsync(Music, false, default);
        Assert.Equal(3, store.Requests.Count);
    }

    [Fact]
    public async Task Stream_urls_are_refetched_only_when_close_to_expiry()
    {
        var (source, store, clock, _) = Make();

        var url = await source.StreamUrlAsync(Release, 1402866115, default);
        Assert.StartsWith("https://t4.bcbits.com/stream/", url);
        await source.StreamUrlAsync(Release, 776289323, default);
        Assert.Single(store.Requests);

        // Metadata is still fresh, but the streams are within an hour of expiring.
        clock.Now += TimeSpan.FromHours(19.5);
        await source.GetReleaseAsync(Release, needStreams: false, default);
        Assert.Single(store.Requests);
        await source.StreamUrlAsync(Release, 1402866115, default);
        Assert.Equal(2, store.Requests.Count);
    }

    [Fact]
    public async Task Unknown_track_has_no_stream()
    {
        var (source, _, _, _) = Make();
        Assert.Null(await source.StreamUrlAsync(Release, 42, default));
    }

    [Fact]
    public async Task The_daily_cap_refuses_and_resets_the_next_day()
    {
        var (source, store, clock, _) = Make(cap: 2);
        await source.ListReleasesAsync(new Uri("https://a.bandcamp.com/music"), false, default);
        await source.ListReleasesAsync(new Uri("https://b.bandcamp.com/music"), false, default);

        await Assert.ThrowsAsync<FetchCapException>(() =>
            source.ListReleasesAsync(new Uri("https://c.bandcamp.com/music"), false, default));
        Assert.Equal(2, store.Requests.Count);

        clock.Now = new DateTimeOffset(clock.Now.UtcDateTime.Date.AddDays(1).AddMinutes(1), TimeSpan.Zero);
        await source.ListReleasesAsync(new Uri("https://c.bandcamp.com/music"), false, default);
        Assert.Equal(3, store.Requests.Count);
        Assert.Equal(1, source.Fetcher.UsedToday);
    }

    [Fact]
    public async Task A_store_error_surfaces_with_its_status()
    {
        var (source, _, _, _) = Make(answer: _ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<FetchFailedException>(() => source.ListReleasesAsync(Music, false, default));
        Assert.Equal(404, e.Status);
    }
}
