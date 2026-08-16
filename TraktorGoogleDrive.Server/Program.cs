using System.Net.Http.Headers;

using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();

// Cloud Run sits in front as a TLS-terminating proxy, so without this the app
// sees scheme=http and the load balancer's IP for every request.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    // The proxy is Google's, not something we enumerate.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

app.UseBlazorFrameworkFiles();

// Blazor fingerprints everything under /_framework, so those may be cached
// hard. Our own root scripts (auth.js, table-resize.js, player.js) are NOT
// fingerprinted, and with no Cache-Control at all browsers fall back to
// heuristic caching — which served a stale auth.js against new WASM and left
// the app stuck on "Checking authentication...". Force revalidation; the ETag
// makes that a cheap 304.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
});

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
///
/// Compares HOST ONLY, deliberately. Cloud Run terminates TLS and forwards
/// plain HTTP, so Request.Scheme is "http" while the browser sends
/// "https://…" in Referer — comparing scheme rejected every real playback with
/// a 403. Host is what actually distinguishes "our page" from "someone else's".
static bool IsSameOrigin(HttpRequest request)
{
    var expectedHost = request.Host.Host;

    static bool HostMatches(string? value, string expected) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, expected, StringComparison.OrdinalIgnoreCase);

    if (request.Headers.TryGetValue(HeaderNames.Origin, out var origin) && !string.IsNullOrEmpty(origin))
        return HostMatches(origin.ToString(), expectedHost);

    // Media elements send Referer but no Origin for same-origin GETs.
    if (request.Headers.TryGetValue(HeaderNames.Referer, out var referer) && !string.IsNullOrEmpty(referer))
        return HostMatches(referer.ToString(), expectedHost);

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

// index.html names the non-fingerprinted scripts, so it must never be served
// from cache without revalidating either.
//
// FileProvider must be set explicitly: passing StaticFileOptions without one
// loses the composite provider that exposes the referenced client project's
// static web assets, and the fallback 404s in `dotnet run`.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    FileProvider = app.Environment.WebRootFileProvider,
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers[HeaderNames.CacheControl] = "no-cache",
});

app.Run();
