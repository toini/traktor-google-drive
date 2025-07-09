using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddHttpClient();

var app = builder.Build();

// Serve Blazor WebAssembly files
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseHttpsRedirection();

// Proxy endpoint
app.MapGet("/api/proxy/drive/{fileId}", async (string fileId, string token, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (!response.IsSuccessStatusCode)
        return Results.StatusCode((int)response.StatusCode);
    var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    var stream = await response.Content.ReadAsStreamAsync();
    return Results.Stream(stream, contentType);
});

// Fallback to index.html for client routes
app.MapFallbackToFile("index.html");

app.Run();
