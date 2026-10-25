using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TraktorGoogleDrive.Server.Cast;

/// <summary>
/// Media tickets for a Cast device (ADR 20261018-63, addendum "Cast media tickets").
///
/// A Chromecast fetches the audio itself and has no cookie of ours, so whatever
/// authorises it has to be in the URL, and Cloud Run logs full URLs. The Google
/// token therefore stays in this process's memory, and the URL carries a ticket:
/// <c>c1|grantId|fileId|exp</c>, HMAC-signed, base64url. The signature scopes the
/// ticket to one file and one hard expiry without any state; the token it stands
/// for is looked up by <c>grantId</c>.
///
/// The sender pushes each renewed token to the same grant (with the renew key,
/// which is never in a URL), so the device's URL keeps working across renewals
/// with no reload. A restart loses the tokens but not the tickets: the secret is
/// stable, so the sender's next renewal or heartbeat puts the token back.
/// </summary>
public sealed partial class CastTickets
{
    /// <summary>Hard cap on one URL. Past it the sender re-issues the load once.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromHours(4);

    /// <summary>A Google access token lives an hour; never hold one longer.</summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>One user casts one track at a time; this only bounds a flood.</summary>
    private const int MaxGrants = 256;

    private const string Version = "c1";
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("traktor-google-drive/cast-ticket/v1");

    private readonly byte[] _key;
    private readonly ConcurrentDictionary<string, Grant> _grants = new();

    private sealed record Grant(string FileId, string Token, DateTimeOffset TokenExpires, DateTimeOffset TicketExpires);

    public sealed record Issued(string Ticket, string RenewKey, DateTimeOffset ExpiresAt);

    public enum Verdict { Ok, Invalid, Expired, WrongFile }

    public readonly record struct Checked(Verdict Verdict, string GrantId, string FileId);

    public CastTickets(IConfiguration config, ILogger<CastTickets> log)
    {
        // The same secret as the remote-control tickets, but never the same key:
        // HKDF with a purpose-specific info string, so a hub ticket can never be
        // replayed as a media ticket or the other way round.
        byte[] secret;
        if (config["RemoteAuth:TicketSecret"] is { Length: >= 32 } configured)
        {
            secret = Encoding.UTF8.GetBytes(configured);
        }
        else
        {
            // As in RemoteAuth: fine for one instance and for development. A
            // restart then invalidates every ticket, and the sender re-issues the
            // load when its renewal is refused.
            secret = RandomNumberGenerator.GetBytes(32);
            log.LogWarning("RemoteAuth:TicketSecret is not set; Cast tickets use a per-process random key");
        }
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: [], info: KeyInfo);
    }

    /// <summary>Drive ids are URL-safe base64-ish; anything else is refused before it is signed.</summary>
    public static bool IsValidFileId(string? fileId) => fileId is not null && FileIdPattern().IsMatch(fileId);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,200}$")]
    private static partial Regex FileIdPattern();

    public Issued Issue(string fileId, string token, DateTimeOffset? tokenExpires)
    {
        if (!IsValidFileId(fileId)) throw new ArgumentException("Not a Drive file id", nameof(fileId));

        var grantId = Base64Url(RandomNumberGenerator.GetBytes(16));
        var exp = DateTimeOffset.UtcNow.Add(TicketLifetime).ToUnixTimeSeconds();
        var expires = DateTimeOffset.FromUnixTimeSeconds(exp);
        var payload = Encoding.UTF8.GetBytes($"{Version}|{grantId}|{fileId}|{exp}");
        var ticket = $"{Base64Url(payload)}.{Base64Url(HMACSHA256.HashData(_key, payload))}";

        Store(grantId, fileId, token, tokenExpires, expires);
        return new Issued(ticket, RenewKeyFor(grantId), expires);
    }

    /// <summary>
    /// Checks the signature, the expiry and, when <paramref name="fileId"/> is
    /// given, that the ticket was issued for that file and no other.
    /// </summary>
    public Checked Validate(string? ticket, string? fileId = null)
    {
        if (string.IsNullOrEmpty(ticket)) return new(Verdict.Invalid, "", "");
        var parts = ticket.Split('.');
        if (parts.Length != 2) return new(Verdict.Invalid, "", "");

        byte[] payload, mac;
        try { payload = FromBase64Url(parts[0]); mac = FromBase64Url(parts[1]); }
        catch (FormatException) { return new(Verdict.Invalid, "", ""); }

        if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(_key, payload)))
            return new(Verdict.Invalid, "", "");

        var fields = Encoding.UTF8.GetString(payload).Split('|');
        if (fields.Length != 4 || fields[0] != Version || !long.TryParse(fields[3], out var exp))
            return new(Verdict.Invalid, "", "");

        var grantId = fields[1];
        var ticketFile = fields[2];
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return new(Verdict.Expired, grantId, ticketFile);
        if (fileId is not null && !string.Equals(fileId, ticketFile, StringComparison.Ordinal))
            return new(Verdict.WrongFile, grantId, ticketFile);
        return new(Verdict.Ok, grantId, ticketFile);
    }

    /// <summary>
    /// Puts a (renewed) token behind an existing ticket. Also how a grant comes
    /// back after a restart: the ticket is still valid, only the token was lost.
    /// </summary>
    public Verdict Renew(string? ticket, string? renewKey, string token, DateTimeOffset? tokenExpires)
    {
        var check = Validate(ticket);
        if (check.Verdict != Verdict.Ok) return check.Verdict;
        if (!RenewKeyMatches(check.GrantId, renewKey)) return Verdict.Invalid;

        Store(check.GrantId, check.FileId, token, tokenExpires, ExpiryOf(ticket!));
        return Verdict.Ok;
    }

    /// <summary>Forgets the token behind a ticket. The ticket itself then gets no further.</summary>
    public Verdict Revoke(string? ticket, string? renewKey)
    {
        var check = Validate(ticket);
        if (check.Verdict is Verdict.Invalid) return Verdict.Invalid;
        if (!RenewKeyMatches(check.GrantId, renewKey)) return Verdict.Invalid;
        _grants.TryRemove(check.GrantId, out _);
        return Verdict.Ok;
    }

    /// <summary>The Google token behind a validated grant, or null when none is held (any more).</summary>
    public string? TokenFor(string grantId)
    {
        if (!_grants.TryGetValue(grantId, out var grant)) return null;
        var now = DateTimeOffset.UtcNow;
        if (grant.TokenExpires <= now || grant.TicketExpires <= now)
        {
            _grants.TryRemove(grantId, out _);
            return null;
        }
        return grant.Token;
    }

    private void Store(string grantId, string fileId, string token, DateTimeOffset? tokenExpires, DateTimeOffset ticketExpires)
    {
        var now = DateTimeOffset.UtcNow;
        var cap = now.Add(MaxTokenLifetime);
        var until = tokenExpires is { } t && t < cap ? t : cap;
        if (until > ticketExpires) until = ticketExpires;
        _grants[grantId] = new Grant(fileId, token, until, ticketExpires);
        Sweep(now);
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var (id, grant) in _grants)
            if (grant.TokenExpires <= now) _grants.TryRemove(id, out _);

        var excess = _grants.Count - MaxGrants;
        if (excess <= 0) return;
        foreach (var (id, _) in _grants.OrderBy(g => g.Value.TokenExpires).Take(excess))
            _grants.TryRemove(id, out _);
    }

    private string RenewKeyFor(string grantId) =>
        Base64Url(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("renew|" + grantId)));

    private bool RenewKeyMatches(string grantId, string? renewKey)
    {
        if (string.IsNullOrEmpty(renewKey)) return false;
        var expected = Encoding.UTF8.GetBytes(RenewKeyFor(grantId));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(renewKey));
    }

    private static DateTimeOffset ExpiryOf(string ticket)
    {
        var fields = Encoding.UTF8.GetString(FromBase64Url(ticket.Split('.')[0])).Split('|');
        return DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[3]));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
