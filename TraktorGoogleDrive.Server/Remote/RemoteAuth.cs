using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TraktorGoogleDrive.Server.Remote;

/// <summary>
/// Who is calling, for the remote-control hub (ADR 20261018-63 §3).
///
/// The Google access token is validated once, when a tab asks for a ticket:
/// tokeninfo proves the token was issued to *this* app's OAuth client and carries
/// <c>drive.readonly</c>, and Drive's <c>about.get</c> gives a stable user id. Both
/// work with the scope the app already has, so no new consent is needed.
///
/// The hub itself only ever sees a short-lived HMAC ticket. Browsers cannot set
/// headers on a WebSocket, so whatever authenticates it ends up in the URL, and
/// Cloud Run logs full URLs; a ticket in a log is dead within a minute, a Google
/// token is not.
/// </summary>
public sealed class RemoteAuth
{
    public const string DriveReadonlyScope = "https://www.googleapis.com/auth/drive.readonly";

    /// <summary>The OAuth client in wwwroot/auth.js. Public, not a secret.</summary>
    public const string DefaultGoogleClientId =
        "219145501841-n6pki0jbvkue0u3vusmnguld6m4fugp9.apps.googleusercontent.com";

    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _http;
    private readonly ILogger<RemoteAuth> _log;
    private readonly byte[] _secret;
    private readonly string _clientId;
    private readonly HashSet<string> _allowed;
    private readonly bool _requireAllowlist;
    private readonly Dictionary<string, string> _fakeUsers;
    private readonly ConcurrentDictionary<string, (UserIdentity User, DateTimeOffset Expires)> _cache = new();

    public sealed record UserIdentity(string UserKey, string Label);

    public RemoteAuth(IConfiguration config, IHostEnvironment env, IHttpClientFactory http, ILogger<RemoteAuth> log)
    {
        _http = http;
        _log = log;
        _clientId = config["RemoteAuth:GoogleClientId"] is { Length: > 0 } id ? id : DefaultGoogleClientId;

        if (config["RemoteAuth:TicketSecret"] is { Length: >= 32 } secret)
        {
            _secret = Encoding.UTF8.GetBytes(secret);
        }
        else
        {
            // Fine while the service runs one instance (max-instances=1): tickets
            // live a minute and rooms live in memory anyway. A second instance
            // would reject this one's tickets, which the client survives by
            // asking for a new one on reconnect.
            _secret = RandomNumberGenerator.GetBytes(32);
            log.LogWarning("RemoteAuth:TicketSecret is not set; using a per-process random secret");
        }

        // Emails or Drive permission ids. Outside Development an empty list lets
        // nobody in: the allowlist is on (ADR 20261018-63, "Decisions"), and a
        // deploy that forgot it should fail closed, not open to every Google
        // account that can sign in to the app.
        _allowed = (config["RemoteAuth:AllowedUsers"] ?? "")
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToHashSet();
        _requireAllowlist = !env.IsDevelopment();
        if (_requireAllowlist && _allowed.Count == 0)
            log.LogWarning("RemoteAuth:AllowedUsers is empty; remote control is refused for every user");

        // "token=user;token2=user2". A test seam for the e2e suite, which cannot
        // intercept the server's own calls to Google. Never in production.
        _fakeUsers = (config["RemoteAuth:FakeUsers"] ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1]);
        if (_fakeUsers.Count > 0 && !env.IsDevelopment())
            throw new InvalidOperationException("RemoteAuth:FakeUsers is a test seam and must not be set outside Development");
    }

    /// <summary>Validates a Google access token. Null when it is not acceptable.</summary>
    public async Task<UserIdentity?> IdentifyAsync(string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        if (_fakeUsers.Count > 0)
            return _fakeUsers.TryGetValue(accessToken, out var fake) ? Allow(fake, fake) : null;

        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessToken)));
        if (_cache.TryGetValue(cacheKey, out var hit) && hit.Expires > DateTimeOffset.UtcNow)
            return hit.User;

        var client = _http.CreateClient();

        using var info = await client.GetAsync(
            $"https://oauth2.googleapis.com/tokeninfo?access_token={Uri.EscapeDataString(accessToken)}", ct);
        if (!info.IsSuccessStatusCode) return null;
        using var infoJson = JsonDocument.Parse(await info.Content.ReadAsStringAsync(ct));
        var root = infoJson.RootElement;

        // Issued to this app, not to some other client the caller controls.
        var aud = Str(root, "aud");
        var azp = Str(root, "azp");
        if (aud != _clientId && azp != _clientId) return null;

        var scopes = (Str(root, "scope") ?? "").Split(' ');
        if (!scopes.Contains(DriveReadonlyScope)) return null;

        if (!long.TryParse(Str(root, "expires_in"), out var expiresIn) || expiresIn <= 0) return null;

        using var about = new HttpRequestMessage(HttpMethod.Get,
            "https://www.googleapis.com/drive/v3/about?fields=user(permissionId,emailAddress)");
        about.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var aboutResponse = await client.SendAsync(about, ct);
        if (!aboutResponse.IsSuccessStatusCode) return null;
        using var aboutJson = JsonDocument.Parse(await aboutResponse.Content.ReadAsStringAsync(ct));
        if (!aboutJson.RootElement.TryGetProperty("user", out var user)) return null;

        var permissionId = Str(user, "permissionId");
        if (string.IsNullOrEmpty(permissionId)) return null;
        var email = Str(user, "emailAddress");

        var identity = Allow(permissionId, email);
        if (identity is null) return null;

        _cache[cacheKey] = (identity, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        return identity;
    }

    private UserIdentity? Allow(string userId, string? email)
    {
        var userKey = Hex(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes("user:" + userId)))[..32];
        if ((_allowed.Count > 0 || _requireAllowlist)
            && !_allowed.Contains(userId.ToLowerInvariant())
            && !(email is not null && _allowed.Contains(email.ToLowerInvariant())))
        {
            // The key prefix is enough to recognise a user in the logs; the
            // Google id and email are not logged.
            _log.LogWarning("Remote control refused for user {UserKey}: not in RemoteAuth:AllowedUsers", userKey[..8]);
            return null;
        }
        return new UserIdentity(userKey, userKey[..8]);
    }

    public string IssueTicket(UserIdentity user)
    {
        var expires = DateTimeOffset.UtcNow.Add(TicketLifetime).ToUnixTimeSeconds();
        var payload = Encoding.UTF8.GetBytes($"{user.UserKey}|{expires}");
        var mac = HMACSHA256.HashData(_secret, payload);
        return $"{Base64Url(payload)}.{Base64Url(mac)}";
    }

    /// <summary>The ticket's user key, or null if forged or expired.</summary>
    public string? ValidateTicket(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket)) return null;
        var parts = ticket.Split('.');
        if (parts.Length != 2) return null;

        byte[] payload, mac;
        try { payload = FromBase64Url(parts[0]); mac = FromBase64Url(parts[1]); }
        catch (FormatException) { return null; }

        if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(_secret, payload))) return null;

        var fields = Encoding.UTF8.GetString(payload).Split('|');
        if (fields.Length != 2 || !long.TryParse(fields[1], out var expires)) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires) return null;
        return fields[0];
    }

    /// <summary>
    /// Strict same-origin check: the header must be present and name this host.
    /// Unlike the audio proxy's check there is no "neither header" pass and no
    /// Cast receiver host; only this app's own pages talk to the hub.
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request) =>
        request.Headers.Origin.ToString() is { Length: > 0 } origin
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
