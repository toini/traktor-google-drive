using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using TraktorGoogleDrive.Server.Remote;

namespace TraktorGoogleDrive.Server.Auth;

/// <summary>
/// Access tokens minted from session refresh tokens, held in memory (ADR 20261101-38 §4).
///
/// One entry per refresh token, keyed by its SHA-256, so the token itself is never
/// a dictionary key or in a log. A cached access token is handed out until it has
/// less than <see cref="RefreshLead"/> left; then one caller refreshes it with Google
/// and every concurrent caller waits for that same refresh (single flight). The page
/// asks <see cref="ClientLead"/> before expiry, which is inside this lead, so its
/// request always gets a fresh hour rather than the token it already holds.
///
/// Every token handed out passes <see cref="RemoteAuth.IdentifyAsync"/>: issued to
/// this client, carrying drive.readonly, and the user on the allowlist. RemoteAuth
/// caches that answer per access token, so the check costs Google one call an hour.
/// </summary>
public sealed class SessionTokens
{
    /// <summary>Refresh once a cached token has less than this left.</summary>
    public static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(5);

    /// <summary>What auth.js uses: renew this long before expiry. Must stay below <see cref="RefreshLead"/>.</summary>
    public static readonly TimeSpan ClientLead = TimeSpan.FromMinutes(4);

    /// <summary>A forced refresh (the page saw a 401) within this of the last one reuses it.</summary>
    public static readonly TimeSpan ForceDebounce = TimeSpan.FromSeconds(30);

    /// <summary>One user, a few devices; this only bounds a flood.</summary>
    private const int MaxEntries = 64;

    private readonly GoogleOAuth _google;
    private readonly RemoteAuth _identity;
    private readonly TimeProvider _time;
    private readonly ILogger<SessionTokens> _log;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<GoogleOAuth.Grant>>> _inflight = new();

    private sealed record Entry(string AccessToken, DateTimeOffset ExpiresAt, DateTimeOffset ObtainedAt);

    public enum Status { Ok, InvalidGrant, NotAllowed, Failed }

    public sealed record Result(Status Status, string? AccessToken = null, long ExpiresIn = 0);

    public SessionTokens(GoogleOAuth google, RemoteAuth identity, ILogger<SessionTokens> log, TimeProvider? time = null)
    {
        _google = google;
        _identity = identity;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Puts the access token from a code exchange in the cache, so the first request needs no refresh.</summary>
    public void Seed(string refreshToken, string accessToken, long expiresIn)
    {
        var now = _time.GetUtcNow();
        _cache[Key(refreshToken)] = new Entry(accessToken, now.AddSeconds(expiresIn), now);
        Sweep(now);
    }

    public void Forget(string refreshToken) => _cache.TryRemove(Key(refreshToken), out _);

    /// <summary>
    /// A usable access token for this refresh token. <paramref name="force"/>: Drive
    /// just refused the cached one, so refresh even if our clock says it is fine.
    /// </summary>
    public async Task<Result> GetAsync(string refreshToken, bool force, CancellationToken ct)
    {
        var key = Key(refreshToken);
        var now = _time.GetUtcNow();

        if (_cache.TryGetValue(key, out var cached)
            && cached.ExpiresAt - now > RefreshLead
            && !(force && now - cached.ObtainedAt > ForceDebounce))
        {
            return await CheckedAsync(cached, ct);
        }

        var flight = _inflight.GetOrAdd(key, _ => new Lazy<Task<GoogleOAuth.Grant>>(() => RefreshAsync(key, refreshToken)));
        GoogleOAuth.Grant grant;
        try
        {
            grant = await flight.Value.WaitAsync(ct);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<GoogleOAuth.Grant>>>(key, flight));
        }

        switch (grant.Status)
        {
            case GoogleOAuth.Status.Ok:
                return _cache.TryGetValue(key, out var fresh) ? await CheckedAsync(fresh, ct) : new Result(Status.Failed);
            case GoogleOAuth.Status.InvalidGrant:
                return new Result(Status.InvalidGrant);
            default:
                // Google is down or slow: a cached token that still works beats none.
                if (_cache.TryGetValue(key, out var stale) && stale.ExpiresAt > _time.GetUtcNow())
                    return await CheckedAsync(stale, ct);
                return new Result(Status.Failed);
        }
    }

    // Not tied to any one caller's cancellation: the others share it.
    private async Task<GoogleOAuth.Grant> RefreshAsync(string key, string refreshToken)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var grant = await _google.RefreshAsync(refreshToken, cts.Token);
        var now = _time.GetUtcNow();
        if (grant.Status == GoogleOAuth.Status.Ok)
        {
            _cache[key] = new Entry(grant.AccessToken!, now.AddSeconds(grant.ExpiresIn), now);
            Sweep(now);
        }
        else if (grant.Status == GoogleOAuth.Status.InvalidGrant)
        {
            _cache.TryRemove(key, out _);
            _log.LogInformation("Session {Session} refresh token is no longer valid (invalid_grant)", key[..8]);
        }
        return grant;
    }

    private async Task<Result> CheckedAsync(Entry entry, CancellationToken ct)
    {
        if (await _identity.IdentifyAsync(entry.AccessToken, ct) is null) return new Result(Status.NotAllowed);
        return new Result(Status.Ok, entry.AccessToken,
            (long)Math.Max(0, (entry.ExpiresAt - _time.GetUtcNow()).TotalSeconds));
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (k, e) in _cache)
            if (e.ExpiresAt <= now) _cache.TryRemove(k, out _);
        var excess = _cache.Count - MaxEntries;
        if (excess <= 0) return;
        foreach (var (k, _) in _cache.OrderBy(e => e.Value.ObtainedAt).Take(excess))
            _cache.TryRemove(k, out _);
    }

    private static string Key(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken))).ToLowerInvariant();
}
