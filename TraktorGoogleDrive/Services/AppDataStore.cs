using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TraktorGoogleDrive.Services;

/// <summary>The token has no <c>drive.appdata</c> scope yet: Toni has to sign in once more to grant it.</summary>
public sealed class AppDataScopeException(string message) : Exception(message);

/// <summary>
/// The Drive <c>appDataFolder</c>: a per-app hidden folder in Toni's own Drive
/// (ADR 20261025-54 §8, S-A). Needs the <c>drive.appdata</c> scope, which auth.js
/// now requests alongside <c>drive.readonly</c>. Nothing here touches his visible Drive.
/// </summary>
public sealed class AppDataStore
{
    private const string Api = "https://www.googleapis.com/drive/v3/files";
    private const string Upload = "https://www.googleapis.com/upload/drive/v3/files";

    private readonly HttpClient _http;

    public AppDataStore(HttpClient http) => _http = http;

    public sealed record AppFile(string Id, string Name, DateTimeOffset? ModifiedTime);

    public async Task<List<AppFile>> ListAsync(string namePrefix, string token)
    {
        var files = new List<AppFile>();
        string? page = null;
        do
        {
            var q = Uri.EscapeDataString($"name contains '{namePrefix.Replace("'", "\\'")}' and trashed = false");
            var url = $"{Api}?spaces=appDataFolder&q={q}&fields=nextPageToken,files(id,name,modifiedTime)&pageSize=200"
                      + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}");
            using var response = await _http.SendAsync(Request(HttpMethod.Get, url, token));
            await ThrowIfFailedAsync(response, "Listing Discover data");
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var f in json.GetProperty("files").EnumerateArray())
            {
                var name = f.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith(namePrefix, StringComparison.Ordinal)) continue;
                DateTimeOffset? modified = f.TryGetProperty("modifiedTime", out var m) && m.TryGetDateTimeOffset(out var mt) ? mt : null;
                files.Add(new AppFile(f.GetProperty("id").GetString()!, name, modified));
            }
            page = json.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
        } while (page is not null);
        return files;
    }

    public async Task<string> ReadAsync(string fileId, string token)
    {
        using var response = await _http.SendAsync(Request(HttpMethod.Get, $"{Api}/{Uri.EscapeDataString(fileId)}?alt=media", token));
        await ThrowIfFailedAsync(response, "Reading Discover data");
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Creates <paramref name="name"/> in appDataFolder. Returns its id.</summary>
    public async Task<string> CreateAsync(string name, string content, string token)
    {
        var metadata = JsonSerializer.Serialize(new { name, parents = new[] { "appDataFolder" }, mimeType = "application/x-ndjson" });
        var multipart = new MultipartContent("related")
        {
            new StringContent(metadata, Encoding.UTF8, "application/json"),
            new StringContent(content, Encoding.UTF8, "application/x-ndjson"),
        };
        var request = Request(HttpMethod.Post, $"{Upload}?uploadType=multipart&fields=id", token);
        request.Content = multipart;
        using var response = await _http.SendAsync(request);
        await ThrowIfFailedAsync(response, "Saving Discover data");
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetString()!;
    }

    /// <summary>Replaces the content of a file this device owns. Drive has no append.</summary>
    public async Task UpdateAsync(string fileId, string content, string token)
    {
        var request = Request(HttpMethod.Patch, $"{Upload}/{Uri.EscapeDataString(fileId)}?uploadType=media&fields=id", token);
        request.Content = new StringContent(content, Encoding.UTF8, "application/x-ndjson");
        using var response = await _http.SendAsync(request);
        await ThrowIfFailedAsync(response, "Saving Discover data");
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, string what)
    {
        if (response.IsSuccessStatusCode) return;
        var body = "";
        try { body = await response.Content.ReadAsStringAsync(); } catch { }
        if (body.Length > 300) body = body[..300] + "…";

        // A token from before this feature has drive.readonly only: Drive answers
        // 403 insufficientPermissions / ACCESS_TOKEN_SCOPE_INSUFFICIENT.
        if (response.StatusCode == HttpStatusCode.Forbidden
            && (body.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
                || body.Contains("SCOPE", StringComparison.Ordinal)))
            throw new AppDataScopeException($"{what}: this sign-in has no permission for app data yet");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new DriveAuthException($"{what}: HTTP 401");
        throw new DriveRequestException($"{what}: HTTP {(int)response.StatusCode} {body}");
    }
}
