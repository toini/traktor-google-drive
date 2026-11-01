using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TraktorGoogleDrive.Server.Auth;

/// <summary>
/// Seals the Google refresh token into the <c>tgd_session</c> cookie (ADR 20261101-38 §3).
///
/// AES-256-GCM under a key derived with HKDF from <c>AuthSession:CookieSecret</c>, never
/// the raw secret and never <c>RemoteAuth:TicketSecret</c>. The cookie value is
/// <c>s1.{kid}.{base64url(nonce ‖ ciphertext ‖ tag)}</c>, where <c>kid</c> is derived
/// from the secret too, so rotating the secret needs no hand-picked key id: put the old
/// secret in <c>AuthSession:PreviousCookieSecrets</c>, the new one in
/// <c>AuthSession:CookieSecret</c>, and every cookie is resealed under the new key on
/// its next token request. The version and kid are authenticated as associated data,
/// so neither can be swapped without the tag failing.
/// </summary>
public sealed class SessionCookie
{
    public const string Name = "tgd_session";
    public const string Path = "/api/";

    /// <summary>About six months, renewed on every token request (sliding).</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);

    /// <summary>Browsers cap a cookie at ~4 KB; anything longer is not ours.</summary>
    private const int MaxValueLength = 3800;

    private const string Version = "s1";
    private static readonly byte[] KeyInfo = Encoding.UTF8.GetBytes("traktor-google-drive/session-cookie/v1/key");
    private static readonly byte[] KidInfo = Encoding.UTF8.GetBytes("traktor-google-drive/session-cookie/v1/kid");

    private readonly (string Kid, byte[] Key) _current;
    private readonly Dictionary<string, byte[]> _all;

    /// <summary>What the cookie holds. Only the server ever sees it decrypted.</summary>
    public sealed record Payload(string RefreshToken, long IssuedAt);

    public SessionCookie(string currentSecret, IEnumerable<string>? previousSecrets = null)
    {
        _current = Derive(currentSecret);
        _all = new Dictionary<string, byte[]> { [_current.Kid] = _current.Key };
        foreach (var previous in previousSecrets ?? [])
        {
            var (kid, key) = Derive(previous);
            _all.TryAdd(kid, key);
        }
    }

    public string CurrentKeyId => _current.Kid;

    public static (string Kid, byte[] Key) Derive(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 32)
            throw new ArgumentException("A cookie secret must be at least 32 characters", nameof(secret));
        var ikm = Encoding.UTF8.GetBytes(secret);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt: [], info: KeyInfo);
        var kid = Convert.ToHexString(HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 4, salt: [], info: KidInfo))
            .ToLowerInvariant();
        return (kid, key);
    }

    public string Protect(Payload payload)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Wire(payload.RefreshToken, payload.IssuedAt));
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(_current.Key, tag.Length))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad(_current.Kid));
        CryptographicOperations.ZeroMemory(plaintext);

        var sealedBytes = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(sealedBytes, 0);
        ciphertext.CopyTo(sealedBytes, nonce.Length);
        tag.CopyTo(sealedBytes, nonce.Length + ciphertext.Length);
        return $"{Version}.{_current.Kid}.{Base64Url(sealedBytes)}";
    }

    /// <summary>The payload, or null for anything forged, tampered, truncated or sealed under an unknown key.</summary>
    public Payload? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength) return null;
        var parts = value.Split('.');
        if (parts.Length != 3 || parts[0] != Version) return null;
        if (!_all.TryGetValue(parts[1], out var key)) return null;

        byte[] sealedBytes;
        try { sealedBytes = FromBase64Url(parts[2]); }
        catch (FormatException) { return null; }

        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (sealedBytes.Length <= nonceSize + tagSize) return null;

        var nonce = sealedBytes.AsSpan(0, nonceSize);
        var ciphertext = sealedBytes.AsSpan(nonceSize, sealedBytes.Length - nonceSize - tagSize);
        var tag = sealedBytes.AsSpan(sealedBytes.Length - tagSize);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(parts[1]));
        }
        catch (CryptographicException)
        {
            return null;
        }

        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(plaintext);
            return wire is { rt.Length: > 0 } ? new Payload(wire.rt, wire.iat) : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>True when the cookie was sealed under an older key and should be resealed.</summary>
    public bool IsStale(string value) => !value.StartsWith($"{Version}.{_current.Kid}.", StringComparison.Ordinal);

    public static CookieOptions Options(DateTimeOffset now) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        MaxAge = Lifetime,
        Expires = now.Add(Lifetime),
        IsEssential = true,
    };

    public static CookieOptions Deleted() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = DateTimeOffset.UnixEpoch,
        MaxAge = TimeSpan.Zero,
    };

    private static byte[] Aad(string kid) => Encoding.UTF8.GetBytes($"{Version}.{kid}.{Name}");

    // Short names: the cookie has a size budget.
    private sealed record Wire(string rt, long iat);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
