using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using TraktorGoogleDrive.Server.Auth;

namespace TraktorGoogleDrive.Tests;

/// <summary>
/// <c>/api/auth/*</c> and the audio proxy in the real host (ADR 20261101-38), with every
/// outbound call (Google's token and revoke endpoints, Drive) answered by
/// <see cref="FakeGoogle"/>.
/// </summary>
public class AuthEndpointTests : IDisposable
{
    public sealed class Host(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        public FakeGoogle Google { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            foreach (var (k, v) in settings) builder.UseSetting(k, v);
            builder.ConfigureTestServices(services =>
                services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => Google)));
        }
    }

    private static Dictionary<string, string?> On(int perMinute = 1000) => new()
    {
        ["GoogleOAuth:ClientSecret"] = FakeGoogle.ClientSecret,
        ["AuthSession:CookieSecret"] = FakeGoogle.CookieSecret,
        ["AuthSession:RateLimitPerMinute"] = perMinute.ToString(),
        ["RemoteAuth:FakeUsers"] = FakeGoogle.FakeUsers,
        ["RemoteAuth:AllowedUsers"] = "toni",
    };

    private readonly List<Host> _hosts = [];

    private Host Start(Dictionary<string, string?> settings)
    {
        var host = new Host(settings);
        _hosts.Add(host);
        return host;
    }

    public void Dispose()
    {
        foreach (var h in _hosts) h.Dispose();
    }

    private static HttpClient Client(Host host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });

    /// <summary>What auth.js sends: same origin, the CSRF header, JSON, and the session cookie if any.</summary>
    private static HttpRequestMessage Post(string path, object? body = null, string? session = null,
        string? origin = "http://localhost", bool csrfHeader = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (csrfHeader) request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (session is not null) request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        return request;
    }

    private static string? SetCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(SessionCookie.Name + "="))
            : null;

    private static string? SessionValue(HttpResponseMessage response) =>
        SetCookie(response) is { } header ? header.Split(';')[0][(SessionCookie.Name.Length + 1)..] : null;

    private static async Task<string> SignIn(HttpClient client, string code = "code-toni")
    {
        using var response = await client.SendAsync(Post(AuthEndpoints.CodePath, new { code }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return SessionValue(response)!;
    }

    [Fact]
    public async Task Config_says_the_code_flow_is_on_only_with_the_secrets()
    {
        var on = await Client(Start(On())).GetFromJsonAsync<JsonElement>(AuthEndpoints.ConfigPath);
        Assert.True(on.GetProperty("codeFlow").GetBoolean());

        var off = await Client(Start([])).GetFromJsonAsync<JsonElement>(AuthEndpoints.ConfigPath);
        Assert.False(off.GetProperty("codeFlow").GetBoolean());
    }

    [Fact]
    public async Task Flag_off_the_endpoints_say_unavailable_and_the_proxy_still_takes_drive_token()
    {
        var host = Start([]);
        var client = Client(host);

        using var code = await client.SendAsync(Post(AuthEndpoints.CodePath, new { code = "code-toni" }));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, code.StatusCode);
        using var token = await client.SendAsync(Post(AuthEndpoints.TokenPath));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, token.StatusCode);
        Assert.Equal("code_flow_unavailable", (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var proxy = new HttpRequestMessage(HttpMethod.Get, "/api/proxy/drive/file-1");
        proxy.Headers.Add("Cookie", "drive_token=page-token");
        using var relayed = await client.SendAsync(proxy);
        Assert.Equal(HttpStatusCode.OK, relayed.StatusCode);
        Assert.Equal("Bearer page-token", host.Google.DriveAuthorizations.Single());
        Assert.Equal(0, host.Google.Count("authorization_code"));
    }

    [Fact]
    public async Task Sign_in_sets_a_sealed_httponly_strict_cookie_and_returns_only_the_access_token()
    {
        var host = Start(On());
        using var response = await Client(host).SendAsync(Post(AuthEndpoints.CodePath, new { code = "code-toni" }));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("fixture-access-token", json.GetProperty("access_token").GetString());
        Assert.True(json.GetProperty("persistent").GetBoolean());
        Assert.DoesNotContain("rt-toni", body);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var cookie = SetCookie(response)!.ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/api/", cookie);
        Assert.Contains("max-age=15552000", cookie); // 180 days
        Assert.DoesNotContain("rt-toni", cookie);
    }

    [Fact]
    public async Task Token_comes_from_the_session_and_slides_the_cookie()
    {
        var host = Start(On());
        var client = Client(host);
        var session = await SignIn(client);

        using var response = await client.SendAsync(Post(AuthEndpoints.TokenPath, session: session));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fixture-access-token", json.GetProperty("access_token").GetString());
        Assert.InRange(json.GetProperty("expires_in").GetInt64(), 3500, 3600);
        Assert.NotNull(SessionValue(response)); // re-issued: another 180 days
        Assert.Equal(0, host.Google.Count("refresh_token")); // the exchange's token was cached
    }

    [Fact]
    public async Task Near_expiry_the_token_endpoint_refreshes_with_Google()
    {
        var host = Start(On());
        host.Google.ExpiresIn = 200; // inside the 5 min lead from the start
        var client = Client(host);
        var session = await SignIn(client);

        for (var i = 1; i <= 3; i++)
        {
            using var response = await client.SendAsync(Post(AuthEndpoints.TokenPath, session: session));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(i, host.Google.Count("refresh_token"));
        }
        Assert.Equal("rt-toni", host.Google.Calls.Last().Form["refresh_token"]);
    }

    [Fact]
    public async Task Invalid_grant_clears_the_cookie_and_says_so()
    {
        var host = Start(On());
        host.Google.ExpiresIn = 200;
        var client = Client(host);
        var session = await SignIn(client);
        host.Google.Revoked["rt-toni"] = true;

        using var response = await client.SendAsync(Post(AuthEndpoints.TokenPath, session: session));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Contains("expires=thu, 01 jan 1970", SetCookie(response)!.ToLowerInvariant());
    }

    [Fact]
    public async Task No_cookie_or_a_forged_one_is_signed_out()
    {
        var client = Client(Start(On()));
        using var none = await client.SendAsync(Post(AuthEndpoints.TokenPath));
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("signed_out", (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        using var forged = await client.SendAsync(Post(AuthEndpoints.TokenPath, session: "s1.00000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.NotNull(SetCookie(forged)); // cleared
    }

    [Fact]
    public async Task A_user_off_the_allowlist_gets_no_cookie_and_the_grant_is_revoked()
    {
        var host = Start(On());
        using var response = await Client(host).SendAsync(Post(AuthEndpoints.CodePath, new { code = "code-mallory" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(SetCookie(response));
        Assert.Equal("rt-mallory", host.Google.RevokeCalls.Single());
    }

    [Fact]
    public async Task A_code_Google_refuses_is_a_400_without_a_cookie()
    {
        using var response = await Client(Start(On())).SendAsync(Post(AuthEndpoints.CodePath, new { code = "code-bogus" }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(SetCookie(response));
    }

    [Fact]
    public async Task No_refresh_token_in_the_exchange_signs_in_without_persisting()
    {
        using var response = await Client(Start(On())).SendAsync(Post(AuthEndpoints.CodePath, new { code = "code-noref" }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("persistent").GetBoolean());
        Assert.Null(SetCookie(response));
    }

    [Theory]
    [InlineData(AuthEndpoints.CodePath)]
    [InlineData(AuthEndpoints.TokenPath)]
    [InlineData(AuthEndpoints.LogoutPath)]
    public async Task Other_origins_no_origin_and_no_csrf_header_are_refused(string path)
    {
        var host = Start(On());
        var client = Client(host);
        var session = await SignIn(client);

        foreach (var request in new[]
        {
            Post(path, new { code = "code-toni" }, session, origin: "https://evil.example"),
            Post(path, new { code = "code-toni" }, session, origin: null),
            Post(path, new { code = "code-toni" }, session, origin: "null"),
            Post(path, new { code = "code-toni" }, session, csrfHeader: false),
        })
        {
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(SetCookie(response));
        }
        Assert.Equal(1, host.Google.Count("authorization_code")); // only SignIn's
        Assert.Empty(host.Google.RevokeCalls);
    }

    [Fact]
    public async Task Logout_revokes_at_Google_and_clears_the_cookie()
    {
        var host = Start(On());
        var client = Client(host);
        var session = await SignIn(client);

        using var response = await client.SendAsync(Post(AuthEndpoints.LogoutPath, session: session));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("rt-toni", host.Google.RevokeCalls.Single());
        Assert.Contains("expires=thu, 01 jan 1970", SetCookie(response)!.ToLowerInvariant());

        // The cookie a stolen copy would replay is dead too: Google refuses the grant.
        using var after = await client.SendAsync(Post(AuthEndpoints.TokenPath, new { force = true }, session));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task The_proxy_uses_the_session_token_over_drive_token()
    {
        var host = Start(On());
        var client = Client(host);
        var session = await SignIn(client);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/proxy/drive/file-1");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}; drive_token=stale-page-token");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer fixture-access-token", host.Google.DriveAuthorizations.Single());
        Assert.Null(SetCookie(response)); // the proxy never writes the session cookie
    }

    [Fact]
    public async Task The_proxy_falls_back_to_drive_token_when_the_session_is_dead()
    {
        var host = Start(On());
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/proxy/drive/file-1");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}=s1.bogus.AAAA; drive_token=page-token");
        using var response = await Client(host).SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer page-token", host.Google.DriveAuthorizations.Single());
    }

    [Fact]
    public async Task The_Cast_route_ignores_the_session_cookie()
    {
        var host = Start(On());
        var client = Client(host);
        var session = await SignIn(client);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/proxy/cast/file-1");
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(host.Google.DriveAuthorizations);
    }

    [Fact]
    public async Task Token_requests_are_rate_limited()
    {
        var client = Client(Start(On(perMinute: 3)));
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var response = await client.SendAsync(Post(AuthEndpoints.TokenPath));
            statuses.Add(response.StatusCode);
        }
        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized,
            HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }
}
