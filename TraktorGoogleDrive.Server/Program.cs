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

/// Cloud Run caps a non-streamed response at 32 MiB, and a DJ set is 1-2 GB.
/// Setting Content-Length makes the response buffered rather than chunked, so
/// forwarding the real length (needed for seeking) tripped that cap and every
/// large file 500'd. The fix is to answer range requests in bounded slices:
/// small enough to stay under the cap, with Content-Range still reporting the
/// true total so the browser knows the duration and can seek.
const long MaxSliceBytes = 8L * 1024 * 1024;

/// Parses "bytes=start-end". Returns the slice to ask Drive for, capped.
static (long Start, long End)? ParseRange(string? header)
{
    if (string.IsNullOrWhiteSpace(header)) return null;

    var m = System.Text.RegularExpressions.Regex.Match(header, @"bytes=(\d+)-(\d*)");
    if (!m.Success) return null;

    var start = long.Parse(m.Groups[1].Value);
    var requestedEnd = m.Groups[2].Success && m.Groups[2].Value.Length > 0
        ? long.Parse(m.Groups[2].Value)
        : long.MaxValue;

    // Chrome opens media with "bytes=0-", i.e. the whole file. Clamp it.
    var end = Math.Min(requestedEnd, start + MaxSliceBytes - 1);
    return (start, end);
}

static void CopyMediaHeaders(HttpResponseMessage upstream, HttpResponse outgoing, bool ranged)
{
    outgoing.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
    outgoing.Headers[HeaderNames.AcceptRanges] = "bytes";

    // The URL carries a bearer token; never let a shared cache keep the body.
    outgoing.Headers[HeaderNames.CacheControl] = "private, no-store";

    if (ranged)
    {
        // A bounded slice: safe to declare a length, and Content-Range carries
        // the true total so the client can seek across the whole file.
        if (upstream.Content.Headers.ContentRange is { } contentRange)
            outgoing.Headers[HeaderNames.ContentRange] = contentRange.ToString();
        if (upstream.Content.Headers.ContentLength is { } length)
            outgoing.ContentLength = length;
        return;
    }

    // Unranged: stream it. Deliberately NO Content-Length — that is what keeps
    // the response chunked and exempt from Cloud Run's 32 MiB limit.
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

    var slice = ParseRange(incomingRequest.Headers[HeaderNames.Range].ToString());
    if (slice is { } s)
        request.Headers.TryAddWithoutValidation(HeaderNames.Range, $"bytes={s.Start}-{s.End}");

    using var upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    if (!upstream.IsSuccessStatusCode)
    {
        // Forward the real status so the client can tell 401 (expired token)
        // from 404 (missing file) instead of guessing.
        logger.LogInformation("Drive returned {Status} for {FileId}", (int)upstream.StatusCode, fileId);
        outgoingResponse.StatusCode = (int)upstream.StatusCode;
        return;
    }

    var ranged = slice is not null && upstream.StatusCode == System.Net.HttpStatusCode.PartialContent;
    outgoingResponse.StatusCode = ranged ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
    CopyMediaHeaders(upstream, outgoingResponse, ranged);

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
