using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using TraktorGoogleDrive.Discover;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// The server's crate endpoints (TraktorGoogleDrive.Server/Discover/CrateEndpoints)
/// and the Bandcamp ones, behind <see cref="ICrateLookup"/> so the resolver can be
/// tested without a server.
/// </summary>
public sealed class CrateApi : ICrateLookup
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AuthService _auth;
    private readonly DiscoverApi _bandcamp;

    public CrateApi(HttpClient http, AuthService auth, DiscoverApi bandcamp)
    {
        _http = http;
        _auth = auth;
        _bandcamp = bandcamp;
    }

    public Task<DiscogsLookupDto> DiscogsAsync(string artist, string title, long? releaseId, CancellationToken ct) =>
        GetAsync<DiscogsLookupDto>(
            $"api/discover/discogs/lookup?artist={Uri.EscapeDataString(artist)}&title={Uri.EscapeDataString(title)}"
            + (releaseId is { } id ? $"&release={id}" : ""), "Discogs", ct);

    public Task<DiscoverApi.ReleasesResponse> BandcampListAsync(string pageUrl, CancellationToken ct) =>
        _bandcamp.ListReleasesAsync(pageUrl, refresh: false);

    public Task<DiscoverApi.Release> BandcampReleaseAsync(string releaseUrl, CancellationToken ct) =>
        _bandcamp.GetReleaseAsync(releaseUrl);

    public async Task<SoundCloudTrackDto?> SoundCloudAsync(string? url, string artist, string title, string? uploader, CancellationToken ct)
    {
        var path = url is not null
            ? $"api/discover/soundcloud/find?url={Uri.EscapeDataString(url)}"
            : $"api/discover/soundcloud/find?artist={Uri.EscapeDataString(artist)}&title={Uri.EscapeDataString(title)}"
              + (string.IsNullOrWhiteSpace(uploader) ? "" : $"&uploader={Uri.EscapeDataString(uploader)}");
        try { return await GetAsync<SoundCloudTrackDto>(path, "SoundCloud", ct); }
        catch (DiscoverApiException e) when (e.Status == 404) { return null; }
    }

    private async Task<T> GetAsync<T>(string path, string store, CancellationToken ct)
    {
        var token = await _auth.GetTokenAsync() ?? throw new DiscoverApiException(401, "Signed out");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct); }
        catch (HttpRequestException e) { throw new DiscoverApiException(0, "The server did not answer: " + e.Message); }

        using (response)
        {
            if (response.IsSuccessStatusCode) return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
            string? error = null;
            try { error = (await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct)).GetProperty("error").GetString(); }
            catch { /* no JSON body */ }
            var message = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => error ?? $"{store}'s request limit is spent; try again later",
                HttpStatusCode.Forbidden => "This account is not allowed to use Discover",
                HttpStatusCode.Unauthorized => "Signed out",
                HttpStatusCode.NotFound => error ?? $"{store} has nothing there",
                HttpStatusCode.BadRequest => error ?? "Not a lookup the server accepts",
                _ => error ?? $"{store} lookup failed (HTTP {(int)response.StatusCode})",
            };
            throw new DiscoverApiException((int)response.StatusCode, message);
        }
    }
}
