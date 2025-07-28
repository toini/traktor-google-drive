using System.Net.Http.Headers;

namespace TraktorGoogleDrive.Server.Services;

public class ProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    public ProxyService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<(Stream? Stream, string? ContentType, int StatusCode)> ProxyDriveFileAsync(string fileId, string token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(fileId))
            return (null, null, 400);
        var client = _httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            return (null, null, (int)response.StatusCode);
        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var stream = await response.Content.ReadAsStreamAsync();
        return (stream, contentType, 200);
    }
}
