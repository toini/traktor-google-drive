using System.Text.Json;

using TraktorGoogleDrive.Server.Remote;

namespace TraktorGoogleDrive.Server.Auth;

/// <summary>
/// The server's half of Google's authorization-code flow (ADR 20261101-38): code
/// exchange, refresh and revocation against Google's OAuth endpoints, with the
/// client secret. Nothing here logs a token, a code or a response body.
///
/// <see cref="IsAvailable"/> is the feature flag. Without both the client secret and
/// a cookie secret the server says so on <c>/api/auth/config</c>, and the page keeps
/// using the token model it always has.
/// </summary>
public sealed class GoogleOAuth
{
    public const string HttpClientName = "google-oauth";
    public const string DefaultTokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string DefaultRevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>What GIS's popup mode uses in place of a real redirect URI.</summary>
    public const string PopupRedirectUri = "postmessage";

    private readonly IHttpClientFactory _http;
    private readonly ILogger<GoogleOAuth> _log;
    private readonly string? _clientSecret;

    public string ClientId { get; }
    public string TokenEndpoint { get; }
    public string RevokeEndpoint { get; }
    public SessionCookie? Cookie { get; }
    public bool IsAvailable => _clientSecret is not null && Cookie is not null;

    public enum Status { Ok, InvalidGrant, Failed }

    /// <summary>One answer from the token endpoint. <see cref="RefreshToken"/> only on a code exchange.</summary>
    public sealed record Grant(Status Status, string? AccessToken = null, long ExpiresIn = 0,
        string? RefreshToken = null, string? Error = null);

    public GoogleOAuth(IConfiguration config, IHostEnvironment env, IHttpClientFactory http, ILogger<GoogleOAuth> log)
    {
        _http = http;
        _log = log;
        ClientId = config["GoogleOAuth:ClientId"] is { Length: > 0 } id
            ? id
            : config["RemoteAuth:GoogleClientId"] is { Length: > 0 } remoteId ? remoteId : RemoteAuth.DefaultGoogleClientId;

        _clientSecret = config["GoogleOAuth:ClientSecret"] is { Length: > 0 } secret ? secret.Trim() : null;

        var cookieSecret = config["AuthSession:CookieSecret"]?.Trim();
        if (cookieSecret is { Length: >= 32 })
        {
            var previous = (config["AuthSession:PreviousCookieSecrets"] ?? "")
                .Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length >= 32);
            Cookie = new SessionCookie(cookieSecret, previous);
        }
        else if (!string.IsNullOrEmpty(cookieSecret))
        {
            log.LogWarning("AuthSession:CookieSecret is shorter than 32 characters; the code flow stays off");
        }

        if (_clientSecret is not null && Cookie is null)
            log.LogWarning("GoogleOAuth:ClientSecret is set but AuthSession:CookieSecret is not; the code flow stays off");
        else if (_clientSecret is null)
            log.LogInformation("GoogleOAuth:ClientSecret is not set; the page uses the token model (no refresh token)");

        // A test seam, like RemoteAuth:FakeUsers: the e2e suite points these at a
        // fake Google. Outside Development a changed endpoint would send the client
        // secret and every refresh token somewhere else, so the server refuses to start.
        var tokenEndpoint = config["GoogleOAuth:TokenEndpoint"];
        var revokeEndpoint = config["GoogleOAuth:RevokeEndpoint"];
        if ((!string.IsNullOrEmpty(tokenEndpoint) || !string.IsNullOrEmpty(revokeEndpoint)) && !env.IsDevelopment())
            throw new InvalidOperationException(
                "GoogleOAuth:TokenEndpoint/RevokeEndpoint are a test seam and must not be set outside Development");
        TokenEndpoint = string.IsNullOrEmpty(tokenEndpoint) ? DefaultTokenEndpoint : tokenEndpoint;
        RevokeEndpoint = string.IsNullOrEmpty(revokeEndpoint) ? DefaultRevokeEndpoint : revokeEndpoint;
    }

    /// <summary>Trades a GIS popup code for access and refresh tokens.</summary>
    public Task<Grant> ExchangeCodeAsync(string code, CancellationToken ct) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = PopupRedirectUri,
        }, ct);

    /// <summary>A fresh access token for a refresh token. <see cref="Status.InvalidGrant"/> when Google no longer honours it.</summary>
    public Task<Grant> RefreshAsync(string refreshToken, CancellationToken ct) =>
        PostTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    /// <summary>
    /// Revokes at Google. Revoking either token ends the whole grant, so every
    /// session (and every access token) of that user for this app stops working.
    /// </summary>
    public async Task<bool> RevokeAsync(string token, CancellationToken ct)
    {
        try
        {
            using var response = await _http.CreateClient(HttpClientName).PostAsync(RevokeEndpoint,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
            if (response.IsSuccessStatusCode) return true;
            _log.LogWarning("Google revoke answered {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Google revoke failed: {Error}", ex.GetType().Name);
            return false;
        }
    }

    private async Task<Grant> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        if (_clientSecret is null) return new Grant(Status.Failed, Error: "not_configured");

        form["client_id"] = ClientId;
        form["client_secret"] = _clientSecret;

        HttpResponseMessage response;
        try
        {
            response = await _http.CreateClient(HttpClientName).PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("Google token endpoint unreachable: {Error}", ex.GetType().Name);
            return new Grant(Status.Failed, Error: "unreachable");
        }

        using (response)
        {
            JsonElement root;
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                root = json.RootElement.Clone();
            }
            catch (JsonException)
            {
                _log.LogWarning("Google token endpoint answered {Status} without JSON", (int)response.StatusCode);
                return new Grant(Status.Failed, Error: "bad_response");
            }

            if (!response.IsSuccessStatusCode)
            {
                // Only Google's error code: the description can echo request details.
                var error = Str(root, "error") ?? "unknown";
                _log.LogInformation("Google token endpoint answered {Status} {Error} for {GrantType}",
                    (int)response.StatusCode, error, form["grant_type"]);
                return new Grant(error == "invalid_grant" ? Status.InvalidGrant : Status.Failed, Error: error);
            }

            var access = Str(root, "access_token");
            if (string.IsNullOrEmpty(access)) return new Grant(Status.Failed, Error: "no_access_token");
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) && s > 0 ? s : 3600;
            return new Grant(Status.Ok, access, expiresIn, Str(root, "refresh_token"));
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
