using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace TraktorGoogleDrive.Services;

public sealed class DiscoverApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The server's Discover endpoints (TraktorGoogleDrive.Server/Discover). The
/// browser cannot read store pages itself (no CORS), so the server fetches them,
/// one at a time and only when asked (ADR 20261025-54, decision 1).
/// </summary>
public sealed class DiscoverApi
{
    public sealed record ReleaseRef(string Url, string Type, long? Id, string Title, string? Artist);
    public sealed record ReleasesResponse(string Source, string Page, List<ReleaseRef> Releases);
    public sealed record Track(long TrackId, int? Number, string Title, string Artist, double Duration,
        string? TrackUrl, string? Isrc, bool Playable);
    public sealed record Release(string Source, string Url, string Type, long Id, string Title, string Artist,
        string? Label, string? LabelUrl, DateTimeOffset? ReleaseDate, List<string> Tags, List<Track> Tracks,
        string? AlbumUrl, int? StreamingLimit);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly AuthService _auth;

    public DiscoverApi(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    public Task<ReleasesResponse> ListReleasesAsync(string seedUrl, bool refresh) =>
        GetAsync<ReleasesResponse>($"api/discover/bandcamp/releases?url={Uri.EscapeDataString(seedUrl)}{(refresh ? "&refresh=true" : "")}");

    public Task<Release> GetReleaseAsync(string releaseUrl) =>
        GetAsync<Release>($"api/discover/bandcamp/release?url={Uri.EscapeDataString(releaseUrl)}");

    /// <summary>The player's media URL for one Bandcamp track: the server answers with a redirect to the stream.</summary>
    public static string AudioUrl(long trackId, string releaseUrl) =>
        $"/api/proxy/discover/bandcamp/{trackId}?release={Uri.EscapeDataString(releaseUrl)}";

    private async Task<T> GetAsync<T>(string path)
    {
        var token = await _auth.GetTokenAsync() ?? throw new DiscoverApiException(401, "Signed out");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request); }
        catch (HttpRequestException e) { throw new DiscoverApiException(0, "The server did not answer: " + e.Message); }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return (await response.Content.ReadFromJsonAsync<T>(Json))!;

            var message = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Today's Bandcamp request cap is spent; try again tomorrow",
                HttpStatusCode.NotFound => "Bandcamp has no such page",
                HttpStatusCode.Forbidden => "This account is not allowed to use Discover",
                HttpStatusCode.Unauthorized => "Signed out",
                HttpStatusCode.BadRequest => "Not a Bandcamp label, artist or release URL",
                _ => $"Bandcamp lookup failed (HTTP {(int)response.StatusCode})",
            };
            throw new DiscoverApiException((int)response.StatusCode, message);
        }
    }
}
