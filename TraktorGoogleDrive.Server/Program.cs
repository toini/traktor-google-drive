using System.Net.Http.Headers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

var app = builder.Build();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// No UseHttpsRedirection: Cloud Run (and any CDN in front) terminates TLS and
// forwards plain HTTP to the container, so redirecting here only risks a loop.

/// Headers worth passing through from Drive so the browser can seek. Without
/// Content-Length and Accept-Ranges the media element cannot range-request, and
/// long sets become unseekable.
static void CopyMediaHeaders(HttpResponseMessage upstream, HttpResponse outgoing)
{
    outgoing.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

    if (upstream.Content.Headers.ContentLength is { } length)
        outgoing.ContentLength = length;

    if (upstream.Content.Headers.ContentRange is { } contentRange)
        outgoing.Headers[HeaderNames.ContentRange] = contentRange.ToString();

    outgoing.Headers[HeaderNames.AcceptRanges] =
        upstream.Headers.AcceptRanges.Count > 0 ? string.Join(",", upstream.Headers.AcceptRanges) : "bytes";

    // The URL carries a bearer token; never let a shared cache keep the body.
    outgoing.Headers[HeaderNames.CacheControl] = "private, no-store";
}

/// The proxy relays with the *caller's* token, so it cannot reach anything the
/// caller could not already reach. What it must not become is an open relay for
/// third-party pages, hence the same-origin check.
static bool IsSameOrigin(HttpRequest request)
{
    var expected = $"{request.Scheme}://{request.Host}";

    if (request.Headers.TryGetValue(HeaderNames.Origin, out var origin) && !string.IsNullOrEmpty(origin))
        return string.Equals(origin, expected, StringComparison.OrdinalIgnoreCase);

    // Media elements often send Referer but no Origin for same-origin GETs.
    if (request.Headers.TryGetValue(HeaderNames.Referer, out var referer)
        && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        return string.Equals($"{refererUri.Scheme}://{refererUri.Authority}", expected, StringComparison.OrdinalIgnoreCase);

    // Neither header: a direct navigation or curl. Allowed — it is the caller's
    // own token — but it is not a cross-site embed either.
    return true;
}

app.MapMethods("/api/proxy/drive/{fileId}", ["GET", "HEAD"], async (
    HttpRequest incomingRequest,
    HttpResponse outgoingResponse,
    string fileId,
    [FromQuery] string? token,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    CancellationToken cancellationToken) =>
{
    var logger = loggerFactory.CreateLogger("DriveProxy");

    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(fileId))
    {
        outgoingResponse.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    if (!IsSameOrigin(incomingRequest))
    {
        logger.LogWarning("Rejected cross-origin proxy request from {Origin}",
            incomingRequest.Headers[HeaderNames.Origin].ToString());
        outgoingResponse.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }

    var client = httpClientFactory.CreateClient();
    var method = HttpMethods.IsHead(incomingRequest.Method) ? HttpMethod.Head : HttpMethod.Get;
    var request = new HttpRequestMessage(method,
        $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}?alt=media");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    if (incomingRequest.Headers.TryGetValue(HeaderNames.Range, out var rangeHeader))
        request.Headers.TryAddWithoutValidation(HeaderNames.Range, rangeHeader.ToString());

    using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    if (!upstream.IsSuccessStatusCode)
    {
        // Forward the real status so the client can tell 401 (expired token)
        // from 404 (missing file) instead of guessing.
        logger.LogInformation("Drive returned {Status} for {FileId}", (int)upstream.StatusCode, fileId);
        outgoingResponse.StatusCode = (int)upstream.StatusCode;
        return;
    }

    outgoingResponse.StatusCode = (int)upstream.StatusCode;
    CopyMediaHeaders(upstream, outgoingResponse);

    if (method == HttpMethod.Head) return;

    await using var stream = await upstream.Content.ReadAsStreamAsync(cancellationToken);
    await stream.CopyToAsync(outgoingResponse.Body, cancellationToken);
});

app.MapFallbackToFile("index.html");

app.Run();
