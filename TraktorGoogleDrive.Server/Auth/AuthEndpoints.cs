using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;

using TraktorGoogleDrive.Server.Remote;

namespace TraktorGoogleDrive.Server.Auth;

/// <summary>
/// Server-held refresh token (ADR 20261101-38): the page signs in once with Google's
/// authorization-code flow, the server keeps the refresh token sealed in an HttpOnly
/// cookie, and every later access token comes from <c>POST /api/auth/token</c>,
/// with no popup.
///
/// The three POSTs are same-origin only (strict Origin check), need
/// <c>X-Requested-With: XMLHttpRequest</c> (a header no cross-site form or simple
/// request can carry), and are rate limited per client address. The session cookie
/// is <c>SameSite=Strict</c>, so a cross-site request does not carry it in the first place.
/// </summary>
public static class AuthEndpoints
{
    public const string ConfigPath = "/api/auth/config";
    public const string CodePath = "/api/auth/code";
    public const string TokenPath = "/api/auth/token";
    public const string LogoutPath = "/api/auth/logout";
    public const string RateLimitPolicy = "auth";

    public sealed record CodeRequest(string? Code);
    public sealed record TokenRequest(bool? Force);

    public static IServiceCollection AddAuthSession(this IServiceCollection services, IConfiguration config)
    {
        services.AddHttpClient(GoogleOAuth.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<GoogleOAuth>();
        services.AddSingleton(sp => new SessionTokens(
            sp.GetRequiredService<GoogleOAuth>(),
            sp.GetRequiredService<RemoteAuth>(),
            sp.GetRequiredService<ILogger<SessionTokens>>(),
            sp.GetService<TimeProvider>()));

        var perMinute = config.GetValue("AuthSession:RateLimitPerMinute", 30);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Per client address (X-Forwarded-For, via UseForwardedHeaders). The page
            // asks about once an hour per tab; 30 a minute is a generous ceiling that
            // still stops a script hammering Google through us.
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = perMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }));
        });
        return services;
    }

    public static void MapAuthSession(this WebApplication app)
    {
        app.UseRateLimiter();

        // Public: whether the code flow is on. No secret in it, no cookie read.
        app.MapGet(ConfigPath, (HttpResponse response, GoogleOAuth google) =>
        {
            response.Headers[HeaderNames.CacheControl] = "no-store";
            return Results.Json(new { codeFlow = google.IsAvailable });
        });

        app.MapPost(CodePath, async (HttpContext http, CodeRequest? body, GoogleOAuth google, SessionTokens tokens,
            RemoteAuth identity, ILoggerFactory loggers, CancellationToken ct) =>
        {
            NoStore(http.Response);
            if (!IsTrustedCaller(http.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!google.IsAvailable) return Unavailable();
            var code = body?.Code;
            if (string.IsNullOrWhiteSpace(code) || code.Length > 2048) return Results.BadRequest();

            var log = loggers.CreateLogger("AuthSession");
            var grant = await google.ExchangeCodeAsync(code, ct);
            if (grant.Status != GoogleOAuth.Status.Ok)
                return Results.Json(new { error = "exchange_failed" }, statusCode: StatusCodes.Status400BadRequest);

            // The allowlist, at the door: a user who may not use the app gets no
            // cookie, and the grant they just gave is handed back.
            if (await identity.IdentifyAsync(grant.AccessToken!, ct) is null)
            {
                await google.RevokeAsync(grant.RefreshToken ?? grant.AccessToken!, ct);
                return Results.Json(new { error = "not_allowed" }, statusCode: StatusCodes.Status403Forbidden);
            }

            var persistent = !string.IsNullOrEmpty(grant.RefreshToken);
            if (persistent)
            {
                tokens.Seed(grant.RefreshToken!, grant.AccessToken!, grant.ExpiresIn);
                WriteCookie(http.Response, google.Cookie!, grant.RefreshToken!, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            }
            else
            {
                // Google only returns a refresh token for a new grant. The page then
                // works for this hour as it always has; signing out (which revokes)
                // and in again gets one.
                log.LogWarning("Code exchange returned no refresh token; this sign-in is not persistent");
            }

            return Results.Json(new
            {
                access_token = grant.AccessToken,
                expires_in = grant.ExpiresIn,
                persistent,
            });
        }).RequireRateLimiting(RateLimitPolicy);

        app.MapPost(TokenPath, async (HttpContext http, TokenRequest? body, GoogleOAuth google, SessionTokens tokens,
            CancellationToken ct) =>
        {
            NoStore(http.Response);
            if (!IsTrustedCaller(http.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!google.IsAvailable) return Unavailable();

            var raw = http.Request.Cookies[SessionCookie.Name];
            var session = google.Cookie!.Unprotect(raw);
            if (session is null)
            {
                if (raw is not null) ClearCookie(http.Response);
                return Results.Json(new { error = "signed_out" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var result = await tokens.GetAsync(session.RefreshToken, body?.Force == true, ct);
            switch (result.Status)
            {
                case SessionTokens.Status.Ok:
                    // Sliding: another 180 days from now, resealed under the current key.
                    WriteCookie(http.Response, google.Cookie, session.RefreshToken, session.IssuedAt);
                    return Results.Json(new { access_token = result.AccessToken, expires_in = result.ExpiresIn });
                case SessionTokens.Status.InvalidGrant:
                    ClearCookie(http.Response);
                    return Results.Json(new { error = "invalid_grant" }, statusCode: StatusCodes.Status401Unauthorized);
                case SessionTokens.Status.NotAllowed:
                    ClearCookie(http.Response);
                    return Results.Json(new { error = "not_allowed" }, statusCode: StatusCodes.Status403Forbidden);
                default:
                    return Results.Json(new { error = "upstream" }, statusCode: StatusCodes.Status502BadGateway);
            }
        }).RequireRateLimiting(RateLimitPolicy);

        app.MapPost(LogoutPath, async (HttpContext http, GoogleOAuth google, SessionTokens tokens, CancellationToken ct) =>
        {
            NoStore(http.Response);
            if (!IsTrustedCaller(http.Request)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            if (google.Cookie?.Unprotect(http.Request.Cookies[SessionCookie.Name]) is { } session)
            {
                tokens.Forget(session.RefreshToken);
                await google.RevokeAsync(session.RefreshToken, ct);
            }
            if (http.Request.Cookies.ContainsKey(SessionCookie.Name)) ClearCookie(http.Response);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimitPolicy);
    }

    /// <summary>
    /// The access token behind the session cookie on this request, for the audio
    /// proxy. Null when there is no (valid) session: the proxy then falls back to
    /// the page-set <c>drive_token</c> cookie. Never writes the cookie.
    /// </summary>
    public static async Task<string?> SessionAccessTokenAsync(HttpRequest request, CancellationToken ct)
    {
        var google = request.HttpContext.RequestServices.GetRequiredService<GoogleOAuth>();
        if (!google.IsAvailable) return null;
        if (google.Cookie!.Unprotect(request.Cookies[SessionCookie.Name]) is not { } session) return null;
        var result = await request.HttpContext.RequestServices.GetRequiredService<SessionTokens>()
            .GetAsync(session.RefreshToken, force: false, ct);
        return result.Status == SessionTokens.Status.Ok ? result.AccessToken : null;
    }

    /// <summary>
    /// Same origin (Origin present and naming this host), and the custom header that
    /// only script on a permitted origin can add. Either alone stops a cross-site form.
    /// </summary>
    public static bool IsTrustedCaller(HttpRequest request) =>
        RemoteAuth.IsSameOrigin(request)
        && string.Equals(request.Headers["X-Requested-With"].ToString(), "XMLHttpRequest", StringComparison.Ordinal);

    private static IResult Unavailable() =>
        Results.Json(new { error = "code_flow_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static void WriteCookie(HttpResponse response, SessionCookie cookie, string refreshToken, long issuedAt) =>
        response.Cookies.Append(SessionCookie.Name,
            cookie.Protect(new SessionCookie.Payload(refreshToken, issuedAt)),
            SessionCookie.Options(DateTimeOffset.UtcNow));

    private static void ClearCookie(HttpResponse response) =>
        response.Cookies.Append(SessionCookie.Name, "", SessionCookie.Deleted());

    private static void NoStore(HttpResponse response) => response.Headers[HeaderNames.CacheControl] = "no-store";
}
