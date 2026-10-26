using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TraktorGoogleDrive.Server.Discover;

namespace TraktorGoogleDrive.Tests;

/// <summary>
/// The real endpoints in the real host, with Bandcamp replaced by recorded pages
/// (ADR 20261025-54 §12: no live calls). RemoteAuth's FakeUsers seam maps the
/// fixture token to the allowlisted user, as the remote-control e2e suite does.
/// </summary>
public class DiscoverEndpointTests : IClassFixture<DiscoverEndpointTests.Host>
{
    public sealed class Host : WebApplicationFactory<Program>
    {
        public List<Uri> Fetched { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("RemoteAuth:FakeUsers", "fixture-access-token=toni");
            builder.UseSetting("RemoteAuth:AllowedUsers", "toni");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_ => new BandcampSource(
                    new PoliteFetcher(new HttpClient(new Recorded(Fetched)), TimeProvider.System, NullLogger.Instance,
                        TimeSpan.Zero, 500, (_, _) => Task.CompletedTask),
                    TimeProvider.System));
            });
        }

        private sealed class Recorded(List<Uri> log) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                log.Add(request.RequestUri!);
                var file = request.RequestUri!.ToString() switch
                {
                    "https://carnivorrecords.bandcamp.com/music" => "carnivorrecords-music.html",
                    "https://carnivorrecords.bandcamp.com/album/internal-motion" => "carnivorrecords-album-internal-motion.html",
                    _ => null,
                };
                return Task.FromResult(file is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Fixtures.Bandcamp(file)) });
            }
        }
    }

    private readonly Host _host;
    public DiscoverEndpointTests(Host host) => _host = host;

    private HttpClient Client(string? bearer = "fixture-access-token")
    {
        var c = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (bearer is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        return c;
    }

    [Fact]
    public async Task Release_list_for_the_allowlisted_user()
    {
        var json = await Client().GetFromJsonAsync<JsonElement>("/api/discover/bandcamp/releases?url=carnivorrecords.bandcamp.com");
        Assert.Equal("https://carnivorrecords.bandcamp.com/music", json.GetProperty("page").GetString());
        Assert.Equal(10, json.GetProperty("releases").GetArrayLength());
    }

    [Fact]
    public async Task Release_detail_has_no_stream_urls_only_whether_playable()
    {
        var response = await Client().GetAsync("/api/discover/bandcamp/release?url=https://carnivorrecords.bandcamp.com/album/internal-motion");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("bcbits", body);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("Carnivor Records", json.GetProperty("label").GetString());
        Assert.True(json.GetProperty("tracks")[0].GetProperty("playable").GetBoolean());
        Assert.Equal("1616 (Original Mix)", json.GetProperty("tracks")[0].GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("someone-else", HttpStatusCode.Forbidden)]
    public async Task Everyone_else_is_refused_before_anything_is_fetched(string? bearer, HttpStatusCode expected)
    {
        var before = _host.Fetched.Count;
        var response = await Client(bearer).GetAsync("/api/discover/bandcamp/releases?url=carnivorrecords.bandcamp.com");
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, _host.Fetched.Count);
    }

    [Theory]
    [InlineData("/api/discover/bandcamp/releases?url=https://bandcamp.com/search?q=x")]
    [InlineData("/api/discover/bandcamp/releases?url=https://evil.example.com/")]
    [InlineData("/api/discover/bandcamp/release?url=https://carnivorrecords.bandcamp.com/api/x")]
    public async Task Urls_outside_the_allowed_shapes_are_refused(string path)
    {
        var before = _host.Fetched.Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync(path)).StatusCode);
        Assert.Equal(before, _host.Fetched.Count);
    }

    private HttpRequestMessage Audio(string query, string? cookie = "drive_token=fixture-access-token", string? referer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, DiscoverEndpoints.AudioPath + query);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (referer is not null) request.Headers.Referrer = new Uri(referer);
        return request;
    }

    private const string Track1616 = "1402866115?release=https%3A%2F%2Fcarnivorrecords.bandcamp.com%2Falbum%2Finternal-motion";

    [Fact]
    public async Task Audio_redirects_to_the_stream_cdn_and_never_caches()
    {
        var response = await Client(null).SendAsync(Audio(Track1616, referer: "http://localhost/discover/H"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(BandcampUrls.IsStreamUrl(response.Headers.Location!.ToString()));
        Assert.Contains("/mp3-128/1402866115", response.Headers.Location!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Audio_probe_answers_with_a_status_not_a_redirect()
    {
        var ok = await Client(null).SendAsync(Audio(Track1616 + "&probe=1"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var missing = await Client(null).SendAsync(Audio("42?release=https%3A%2F%2Fcarnivorrecords.bandcamp.com%2Falbum%2Finternal-motion&probe=1"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Audio_needs_the_cookie_and_our_own_pages()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).SendAsync(Audio(Track1616, cookie: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(null).SendAsync(Audio(Track1616, referer: "https://evil.example.com/page"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client(null).SendAsync(Audio("1402866115?release=https%3A%2F%2Fevil.example.com%2Falbum%2Fx"))).StatusCode);
    }
}
