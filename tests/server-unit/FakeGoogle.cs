using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using TraktorGoogleDrive.Server.Auth;
using TraktorGoogleDrive.Server.Remote;

namespace TraktorGoogleDrive.Tests;

/// <summary>
/// Google's token and revoke endpoints, and Drive's media endpoint, in memory.
/// Codes are <c>code-{user}</c>; <c>code-noref</c> answers without a refresh token.
/// Access tokens come from <see cref="NextAccessToken"/>, which the tests map to users
/// with RemoteAuth's FakeUsers seam, so no call ever leaves the process.
/// </summary>
public sealed class FakeGoogle : HttpMessageHandler
{
    public const string ClientSecret = "fake-client-secret";
    public const string CookieSecret = "a-cookie-secret-that-is-at-least-32-characters";
    public const string FakeUsers = "fixture-access-token=toni;renewed-access-token=toni;other-user-token=mallory";

    public ConcurrentQueue<(string Path, Dictionary<string, string> Form, string? Authorization)> Calls { get; } = new();
    public ConcurrentDictionary<string, bool> Revoked { get; } = new();
    public long ExpiresIn { get; set; } = 3600;
    public bool Down { get; set; }
    /// <summary>Held while set, to make concurrent refreshes overlap.</summary>
    public TaskCompletionSource? Gate { get; set; }
    private int _issued;

    public string NextAccessToken() =>
        Interlocked.Increment(ref _issued) % 2 == 1 ? "fixture-access-token" : "renewed-access-token";

    public int Count(string grantType) =>
        Calls.Count(c => c.Path == "/token" && c.Form.GetValueOrDefault("grant_type") == grantType);

    public IEnumerable<string> RevokeCalls => Calls.Where(c => c.Path == "/revoke").Select(c => c.Form["token"]);

    public IEnumerable<string?> DriveAuthorizations => Calls.Where(c => c.Path.StartsWith("/drive/")).Select(c => c.Authorization);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var form = request.Content is FormUrlEncodedContent
            ? (await request.Content.ReadAsStringAsync(ct)).Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.ElementAtOrDefault(1) ?? ""))
            : [];
        Calls.Enqueue((uri.AbsolutePath, form, request.Headers.Authorization?.ToString()));

        if (uri.Host == "www.googleapis.com" && uri.AbsolutePath.StartsWith("/drive/"))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audio")) };

        if (uri.Host != "oauth2.googleapis.com") return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (Gate is { } gate) await gate.Task;
        if (Down) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = Json(new { error = "backend" }) };

        if (uri.AbsolutePath == "/revoke")
        {
            Revoked[form["token"]] = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Json(new { }) };
        }

        if (form.GetValueOrDefault("client_secret") != ClientSecret)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = Json(new { error = "invalid_client" }) };

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
                if (form.GetValueOrDefault("redirect_uri") != GoogleOAuth.PopupRedirectUri)
                    return Error("redirect_uri_mismatch");
                return form["code"] switch
                {
                    "code-toni" => Ok("fixture-access-token", "rt-toni"),
                    "code-mallory" => Ok("other-user-token", "rt-mallory"),
                    "code-noref" => Ok("fixture-access-token", null),
                    _ => Error("invalid_grant"),
                };
            case "refresh_token":
                var rt = form["refresh_token"];
                return Revoked.ContainsKey(rt) || !rt.StartsWith("rt-") ? Error("invalid_grant") : Ok(NextAccessToken(), null);
            default:
                return Error("unsupported_grant_type");
        }
    }

    private HttpResponseMessage Ok(string access, string? refresh) => new(HttpStatusCode.OK)
    {
        Content = Json(refresh is null
            ? new Dictionary<string, object> { ["access_token"] = access, ["expires_in"] = ExpiresIn, ["token_type"] = "Bearer" }
            : new Dictionary<string, object>
            {
                ["access_token"] = access, ["expires_in"] = ExpiresIn, ["token_type"] = "Bearer", ["refresh_token"] = refresh,
            }),
    };

    private static HttpResponseMessage Error(string error) =>
        new(HttpStatusCode.BadRequest) { Content = Json(new { error, error_description = "fake" }) };

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    /// <summary>A real GoogleOAuth + RemoteAuth pair talking to this fake, for unit tests without a host.</summary>
    public (GoogleOAuth Google, RemoteAuth Identity) Services(
        string environment = "Development", Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["GoogleOAuth:ClientSecret"] = ClientSecret,
            ["AuthSession:CookieSecret"] = CookieSecret,
            ["RemoteAuth:FakeUsers"] = FakeUsers,
            ["RemoteAuth:AllowedUsers"] = "toni",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var env = new Env(environment);
        var factory = new Factory(this);
        return (new GoogleOAuth(config, env, factory, NullLogger<GoogleOAuth>.Instance),
            new RemoteAuth(config, env, factory, NullLogger<RemoteAuth>.Instance));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "TraktorGoogleDrive.Server";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

/// <summary>A clock the test moves by hand.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
