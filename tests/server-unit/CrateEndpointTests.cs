using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using TraktorGoogleDrive.Server.Discover;

namespace TraktorGoogleDrive.Tests;

/// <summary>The crate endpoints in the real host, with Discogs and SoundCloud replaced by recorded answers.</summary>
public class CrateEndpointTests : IClassFixture<CrateEndpointTests.Host>
{
    public sealed class Host : WebApplicationFactory<Program>
    {
        public List<string> Fetched { get; } = [];

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("RemoteAuth:FakeUsers", "fixture-access-token=toni");
            builder.UseSetting("RemoteAuth:AllowedUsers", "toni");
            builder.ConfigureServices(services =>
            {
                PoliteFetcher Fetcher() => new(new HttpClient(new Recorded(Fetched)), TimeProvider.System, NullLogger.Instance,
                    TimeSpan.Zero, 1000, (_, _) => Task.CompletedTask, accept: "application/json");
                services.AddSingleton(_ => new DiscogsSource(Fetcher(), TimeProvider.System));
                services.AddSingleton(_ => new SoundCloudSource(Fetcher(), TimeProvider.System));
            });
        }

        private sealed class Recorded(List<string> log) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var url = request.RequestUri!.ToString();
                log.Add(url);
                string? body = null;
                if (url == DiscogsSource.SearchUrl("Tengri", "Patch Hell").ToString()) body = Fixtures.Discogs("search-tengri-patch-hell.json");
                else if (url == DiscogsSource.ReleaseUrl(29681248).ToString()) body = Fixtures.Discogs("release-29681248-testimony-20.json");
                else if (url == DiscogsSource.LabelUrl(10916).ToString()) body = Fixtures.Discogs("label-10916-parvati.json");
                else if (url == SoundCloudUrls.OEmbed("https://soundcloud.com/codex-spiralis/something-different").ToString())
                    body = Fixtures.SoundCloud("oembed-codex-spiralis-something-different.json");
                return Task.FromResult(body is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }
        }
    }

    private readonly Host _host;
    public CrateEndpointTests(Host host) => _host = host;

    private HttpClient Client(string? bearer = "fixture-access-token")
    {
        var c = _host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (bearer is not null) c.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        return c;
    }

    [Fact]
    public async Task Discogs_lookup_for_the_allowlisted_user()
    {
        var json = await Client().GetFromJsonAsync<JsonElement>("/api/discover/discogs/lookup?artist=Tengri%20%26%20Jahbo&title=Patch%20Hell");
        var best = json.GetProperty("best");
        Assert.Equal("Testimony 20", best.GetProperty("title").GetString());
        Assert.Equal("https://beatspace-parvati.bandcamp.com/music", best.GetProperty("bandcampUrls")[0].GetString());
        Assert.False(json.GetProperty("ambiguous").GetBoolean());
    }

    [Fact]
    public async Task SoundCloud_by_url_and_by_guess()
    {
        var byUrl = await Client().GetFromJsonAsync<JsonElement>(
            "/api/discover/soundcloud/find?url=https%3A%2F%2Fsoundcloud.com%2Fcodex-spiralis%2Fsomething-different");
        Assert.Equal("Something Different", byUrl.GetProperty("title").GetString());

        var guessed = await Client().GetAsync("/api/discover/soundcloud/find?artist=Codex%20Spiralis&title=Something%20Different&uploader=Codex%20Spiralis");
        Assert.Equal(HttpStatusCode.OK, guessed.StatusCode);

        var missing = await Client().GetAsync("/api/discover/soundcloud/find?artist=Nobody&title=Nothing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("someone-else", HttpStatusCode.Forbidden)]
    public async Task Everyone_else_is_refused_before_anything_is_fetched(string? bearer, HttpStatusCode expected)
    {
        var before = _host.Fetched.Count;
        Assert.Equal(expected, (await Client(bearer).GetAsync("/api/discover/discogs/lookup?artist=A&title=B")).StatusCode);
        Assert.Equal(expected, (await Client(bearer).GetAsync("/api/discover/soundcloud/find?artist=A&title=B")).StatusCode);
        Assert.Equal(before, _host.Fetched.Count);
    }

    [Theory]
    [InlineData("/api/discover/soundcloud/find?url=https%3A%2F%2Fevil.example%2Fa%2Fb")]
    [InlineData("/api/discover/soundcloud/find?url=https%3A%2F%2Fsoundcloud.com%2Fsearch%3Fq%3Dx")]
    [InlineData("/api/discover/discogs/lookup?artist=&title=x")]
    public async Task Bad_input_is_refused_without_a_request(string path)
    {
        var before = _host.Fetched.Count;
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync(path)).StatusCode);
        Assert.Equal(before, _host.Fetched.Count);
    }
}
