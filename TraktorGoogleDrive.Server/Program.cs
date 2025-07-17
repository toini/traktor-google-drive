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
app.MapGet("/api/proxy/drive/{fileId}", async (HttpRequest incomingRequest, HttpResponse outgoingResponse, string fileId, string token, IHttpClientFactory httpClientFactory) =>
{
    var client = httpClientFactory.CreateClient();
    var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // Forward Range header if present
    if (incomingRequest.Headers.TryGetValue("Range", out var rangeHeader))
    {
        Console.WriteLine($"[Proxy] Range header: {rangeHeader}");
        request.Headers.Add("Range", rangeHeader.ToString());
    }

    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (!response.IsSuccessStatusCode)
    {
        outgoingResponse.StatusCode = (int)response.StatusCode;
        return;
    }

    var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    outgoingResponse.ContentType = contentType;

    // Forward Content-Range header if present
    if (response.Content.Headers.Contains("Content-Range"))
    {
        var contentRange = response.Content.Headers.GetValues("Content-Range").FirstOrDefault();
        if (contentRange != null)
            outgoingResponse.Headers["Content-Range"] = contentRange;
        outgoingResponse.StatusCode = 206; // Partial Content
    }

    using var stream = await response.Content.ReadAsStreamAsync();
    await stream.CopyToAsync(outgoingResponse.Body);
});

// Fallback to index.html for client routes
app.MapFallbackToFile("index.html");

app.Run();
