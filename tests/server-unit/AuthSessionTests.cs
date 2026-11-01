using System.Text;

using Microsoft.Extensions.Logging.Abstractions;

using TraktorGoogleDrive.Server.Auth;

namespace TraktorGoogleDrive.Tests;

/// <summary>The sealed session cookie (ADR 20261101-38 §3).</summary>
public class SessionCookieTests
{
    private const string Secret = FakeGoogle.CookieSecret;
    private const string NewSecret = "a-different-cookie-secret-also-32-characters-long";

    [Fact]
    public void Round_trips_the_refresh_token_without_it_appearing_in_the_value()
    {
        var cookie = new SessionCookie(Secret);
        var value = cookie.Protect(new SessionCookie.Payload("1//refresh-token-value", 1_700_000_000));

        Assert.DoesNotContain("refresh-token-value", value);
        Assert.StartsWith($"s1.{cookie.CurrentKeyId}.", value);
        Assert.Equal(new SessionCookie.Payload("1//refresh-token-value", 1_700_000_000), cookie.Unprotect(value));
    }

    [Fact]
    public void Two_seals_of_the_same_payload_differ()
    {
        var cookie = new SessionCookie(Secret);
        var payload = new SessionCookie.Payload("rt", 1);
        Assert.NotEqual(cookie.Protect(payload), cookie.Protect(payload));
    }

    [Fact]
    public void Any_flipped_bit_is_rejected()
    {
        var cookie = new SessionCookie(Secret);
        var value = cookie.Protect(new SessionCookie.Payload("rt-toni", 1));
        var body = value[(value.LastIndexOf('.') + 1)..];
        var prefix = value[..(value.LastIndexOf('.') + 1)];

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i] == 'A' ? 'B' : 'A';
            var tampered = prefix + body[..i] + c + body[(i + 1)..];
            Assert.Null(cookie.Unprotect(tampered));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("s1.deadbeef.AAAA")]
    [InlineData("s1..")]
    [InlineData("s2.x.y")]
    [InlineData("s1.deadbeef.!!!not-base64!!!")]
    public void Garbage_is_rejected(string? value) => Assert.Null(new SessionCookie(Secret).Unprotect(value));

    [Fact]
    public void A_swapped_version_or_key_id_fails_the_tag()
    {
        var cookie = new SessionCookie(Secret, [NewSecret]);
        var value = cookie.Protect(new SessionCookie.Payload("rt", 1));
        var otherKid = SessionCookie.Derive(NewSecret).Kid;
        var swapped = value.Replace($".{cookie.CurrentKeyId}.", $".{otherKid}.");
        Assert.Null(cookie.Unprotect(swapped));
    }

    [Fact]
    public void Another_secret_cannot_open_it()
    {
        var value = new SessionCookie(Secret).Protect(new SessionCookie.Payload("rt", 1));
        Assert.Null(new SessionCookie(NewSecret).Unprotect(value));
    }

    [Fact]
    public void Rotation_opens_cookies_sealed_under_the_previous_secret_and_reseals_under_the_new_one()
    {
        var old = new SessionCookie(Secret).Protect(new SessionCookie.Payload("rt", 1));
        var rotated = new SessionCookie(NewSecret, [Secret]);

        Assert.Equal("rt", rotated.Unprotect(old)?.RefreshToken);
        Assert.True(rotated.IsStale(old));
        var resealed = rotated.Protect(new SessionCookie.Payload("rt", 1));
        Assert.False(rotated.IsStale(resealed));
        Assert.NotEqual(SessionCookie.Derive(Secret).Kid, rotated.CurrentKeyId);

        // Once the old secret is dropped, its cookies are dead.
        Assert.Null(new SessionCookie(NewSecret).Unprotect(old));
    }

    [Fact]
    public void The_key_is_derived_never_the_raw_secret()
    {
        var (_, key) = SessionCookie.Derive(Secret);
        Assert.NotEqual(Encoding.UTF8.GetBytes(Secret)[..32], key);
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void A_short_secret_is_refused() =>
        Assert.Throws<ArgumentException>(() => new SessionCookie("too-short"));

    [Fact]
    public void Cookie_options_are_httponly_secure_strict_and_scoped_to_api()
    {
        var o = SessionCookie.Options(DateTimeOffset.UtcNow);
        Assert.True(o.HttpOnly);
        Assert.True(o.Secure);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Strict, o.SameSite);
        Assert.Equal("/api/", o.Path);
        Assert.Equal(TimeSpan.FromDays(180), o.MaxAge);
    }
}

/// <summary>Refreshing, caching and the allowlist behind <c>/api/auth/token</c>.</summary>
public class SessionTokensTests
{
    private readonly FakeGoogle _google = new();
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionTokens _tokens;

    public SessionTokensTests()
    {
        var (google, identity) = _google.Services();
        _tokens = new SessionTokens(google, identity, NullLogger<SessionTokens>.Instance, _time);
    }

    [Fact]
    public async Task A_seeded_token_is_served_from_the_cache_without_calling_Google()
    {
        _tokens.Seed("rt-toni", "fixture-access-token", 3600);
        var result = await _tokens.GetAsync("rt-toni", force: false, default);

        Assert.Equal(SessionTokens.Status.Ok, result.Status);
        Assert.Equal("fixture-access-token", result.AccessToken);
        Assert.Equal(3600, result.ExpiresIn);
        Assert.Equal(0, _google.Count("refresh_token"));
    }

    [Fact]
    public async Task Refreshes_once_the_cached_token_is_inside_the_lead_and_never_before()
    {
        _tokens.Seed("rt-toni", "fixture-access-token", 3600);

        _time.Advance(TimeSpan.FromMinutes(54)); // 6 min left: still cached
        Assert.Equal("fixture-access-token", (await _tokens.GetAsync("rt-toni", false, default)).AccessToken);
        Assert.Equal(0, _google.Count("refresh_token"));

        _time.Advance(TimeSpan.FromMinutes(2)); // 4 min left: what auth.js asks at
        var renewed = await _tokens.GetAsync("rt-toni", false, default);
        Assert.Equal(1, _google.Count("refresh_token"));
        Assert.Equal(SessionTokens.Status.Ok, renewed.Status);
        Assert.Equal(3600, renewed.ExpiresIn);
    }

    [Fact]
    public async Task An_empty_cache_after_a_restart_refreshes()
    {
        var result = await _tokens.GetAsync("rt-toni", false, default);
        Assert.Equal(SessionTokens.Status.Ok, result.Status);
        Assert.Equal(1, _google.Count("refresh_token"));
    }

    [Fact]
    public async Task Invalid_grant_is_reported_and_nothing_stays_cached()
    {
        _tokens.Seed("rt-toni", "fixture-access-token", 100); // already inside the lead
        _google.Revoked["rt-toni"] = true;

        Assert.Equal(SessionTokens.Status.InvalidGrant, (await _tokens.GetAsync("rt-toni", false, default)).Status);
        // Not a cached answer: a second request asks Google again.
        Assert.Equal(SessionTokens.Status.InvalidGrant, (await _tokens.GetAsync("rt-toni", false, default)).Status);
        Assert.Equal(2, _google.Count("refresh_token"));
    }

    [Fact]
    public async Task Force_bypasses_the_cache_but_not_twice_within_the_debounce()
    {
        _tokens.Seed("rt-toni", "fixture-access-token", 3600);
        _time.Advance(TimeSpan.FromMinutes(1));

        await _tokens.GetAsync("rt-toni", force: true, default);
        Assert.Equal(1, _google.Count("refresh_token"));

        // A burst of 401s from the waveform sampler: one refresh, not one each.
        _time.Advance(TimeSpan.FromSeconds(5));
        await _tokens.GetAsync("rt-toni", force: true, default);
        Assert.Equal(1, _google.Count("refresh_token"));
    }

    [Fact]
    public async Task Concurrent_requests_share_one_refresh()
    {
        _google.Gate = new TaskCompletionSource();
        var all = Enumerable.Range(0, 5).Select(_ => _tokens.GetAsync("rt-toni", false, default)).ToArray();
        await Task.Delay(50);
        _google.Gate.SetResult();
        var results = await Task.WhenAll(all);

        Assert.Equal(1, _google.Count("refresh_token"));
        Assert.All(results, r => Assert.Equal(SessionTokens.Status.Ok, r.Status));
        Assert.Single(results.Select(r => r.AccessToken).Distinct());
    }

    [Fact]
    public async Task The_allowlist_is_checked_on_every_token_served()
    {
        // A refresh token whose access tokens belong to a user not on the list.
        _tokens.Seed("rt-mallory", "other-user-token", 3600);
        Assert.Equal(SessionTokens.Status.NotAllowed, (await _tokens.GetAsync("rt-mallory", false, default)).Status);
    }

    [Fact]
    public async Task Google_down_serves_a_cached_token_that_still_works_and_fails_without_one()
    {
        _tokens.Seed("rt-toni", "fixture-access-token", 200);
        _google.Down = true;

        var result = await _tokens.GetAsync("rt-toni", false, default);
        Assert.Equal(SessionTokens.Status.Ok, result.Status);
        Assert.Equal("fixture-access-token", result.AccessToken);

        _time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(SessionTokens.Status.Failed, (await _tokens.GetAsync("rt-toni", false, default)).Status);
    }
}

/// <summary>The feature flag and the test seam's guard.</summary>
public class GoogleOAuthConfigTests
{
    [Fact]
    public void On_with_both_secrets()
    {
        var (google, _) = new FakeGoogle().Services();
        Assert.True(google.IsAvailable);
    }

    [Fact]
    public void Off_without_the_client_secret()
    {
        var (google, _) = new FakeGoogle().Services(extra: new() { ["GoogleOAuth:ClientSecret"] = null });
        Assert.False(google.IsAvailable);
    }

    [Fact]
    public void Off_without_a_cookie_secret_or_with_a_short_one()
    {
        Assert.False(new FakeGoogle().Services(extra: new() { ["AuthSession:CookieSecret"] = null }).Google.IsAvailable);
        Assert.False(new FakeGoogle().Services(extra: new() { ["AuthSession:CookieSecret"] = "short" }).Google.IsAvailable);
    }

    [Fact]
    public void A_fake_token_endpoint_is_refused_outside_Development()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new FakeGoogle().Services("Production",
            new() { ["GoogleOAuth:TokenEndpoint"] = "http://localhost:1/token" }));
        Assert.Contains("test seam", ex.Message);
    }

    [Fact]
    public void Production_uses_Googles_endpoints()
    {
        var (google, _) = new FakeGoogle().Services("Production", new() { ["RemoteAuth:FakeUsers"] = null });
        Assert.Equal(GoogleOAuth.DefaultTokenEndpoint, google.TokenEndpoint);
        Assert.Equal(GoogleOAuth.DefaultRevokeEndpoint, google.RevokeEndpoint);
    }

    [Fact]
    public async Task Refresh_maps_invalid_grant_and_sends_the_client_secret()
    {
        var fake = new FakeGoogle();
        var (google, _) = fake.Services();
        fake.Revoked["rt-toni"] = true;

        Assert.Equal(GoogleOAuth.Status.InvalidGrant, (await google.RefreshAsync("rt-toni", default)).Status);
        var call = Assert.Single(fake.Calls);
        Assert.Equal(FakeGoogle.ClientSecret, call.Form["client_secret"]);
        Assert.Equal(RemoteAuthClientId, call.Form["client_id"]);
    }

    private const string RemoteAuthClientId = TraktorGoogleDrive.Server.Remote.RemoteAuth.DefaultGoogleClientId;
}
