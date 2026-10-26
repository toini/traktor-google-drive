using Microsoft.Net.Http.Headers;

using TraktorGoogleDrive.Server.Remote;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>
/// Track discovery's server side (ADR 20261025-54). The browser cannot read
/// Bandcamp's pages (no CORS), so the server fetches them, politely, for the one
/// allowlisted user. It never fetches on its own: every request here is the
/// answer to something Toni did in the Discover view.
/// </summary>
public static class DiscoverEndpoints
{
    /// <summary>Under /api/proxy/ so the browser sends the <c>drive_token</c> cookie with a media request.</summary>
    public const string AudioPath = "/api/proxy/discover/bandcamp/";

    public sealed record ReleaseRefDto(string Url, string Type, long? Id, string Title, string? Artist);

    public sealed record ReleasesDto(string Source, string Page, IReadOnlyList<ReleaseRefDto> Releases);

    public sealed record TrackDto(long TrackId, int? Number, string Title, string Artist, double Duration,
        string? TrackUrl, string? Isrc, bool Playable);

    public sealed record ReleaseDto(string Source, string Url, string Type, long Id, string Title, string Artist,
        string? Label, string? LabelUrl, DateTimeOffset? ReleaseDate, IReadOnlyList<string> Tags,
        IReadOnlyList<TrackDto> Tracks, string? AlbumUrl, int? StreamingLimit);

    public static IServiceCollection AddDiscover(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient("bandcamp", c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // A redirect off bandcamp.com is not followed; the parser gets the 3xx as an error.
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var spacing = TimeSpan.FromSeconds(Math.Max(2, config.GetValue("Discover:Bandcamp:MinSpacingSeconds", 2.0)));
            var cap = Math.Clamp(config.GetValue("Discover:Bandcamp:DailyCap", 500), 1, 500);
            return new BandcampSource(
                new PoliteFetcher(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("bandcamp"),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Discover.Bandcamp"),
                    spacing, cap),
                sp.GetRequiredService<TimeProvider>());
        });
        return services;
    }

    public static void MapDiscover(this WebApplication app)
    {
        var api = app.MapGroup("/api/discover");

        api.MapGet("/status", async (HttpRequest request, RemoteAuth auth, BandcampSource bandcamp, CancellationToken ct) =>
        {
            if (await Authorize(request, auth, ct) is { } refused) return refused;
            return Results.Ok(new { bandcamp = new { usedToday = bandcamp.Fetcher.UsedToday, dailyCap = bandcamp.Fetcher.DailyCap } });
        });

        api.MapGet("/bandcamp/releases", async (HttpRequest request, string? url, bool? refresh,
            RemoteAuth auth, BandcampSource bandcamp, CancellationToken ct) =>
        {
            if (await Authorize(request, auth, ct) is { } refused) return refused;
            if (BandcampUrls.MusicPage(url) is not { } page)
                return Results.BadRequest(new { error = "Not a Bandcamp label or artist URL" });

            return await Fetch(async () =>
            {
                var items = await bandcamp.ListReleasesAsync(page, refresh == true, ct);
                return Results.Ok(new ReleasesDto("bandcamp", page.ToString(),
                    items.Select(i => new ReleaseRefDto(i.Url, i.Type, i.Id, i.Title, i.Artist)).ToList()));
            });
        });

        api.MapGet("/bandcamp/release", async (HttpRequest request, string? url,
            RemoteAuth auth, BandcampSource bandcamp, CancellationToken ct) =>
        {
            if (await Authorize(request, auth, ct) is { } refused) return refused;
            if (BandcampUrls.ReleasePage(url) is not { } page)
                return Results.BadRequest(new { error = "Not a Bandcamp album or track URL" });

            return await Fetch(async () =>
            {
                var release = await bandcamp.GetReleaseAsync(page, needStreams: false, ct);
                return release is null
                    ? Results.NotFound(new { error = "No release data on that page" })
                    : Results.Ok(ToDto(release));
            });
        });

        // The player's media URL. Bytes never pass through here: the answer is a
        // redirect to Bandcamp's stream CDN, so there is no Cloud Run egress and we
        // never re-serve anyone's audio (ADR §2.2, P2). ?probe=1 answers with the
        // status instead, because a media element never reveals why it failed and
        // a probing fetch() cannot follow a cross-origin redirect it may not read.
        app.MapMethods(AudioPath + "{trackId:long}", ["GET", "HEAD"], async (
            HttpRequest request, HttpResponse response, long trackId, string? release, string? probe,
            RemoteAuth auth, BandcampSource bandcamp, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var log = loggers.CreateLogger("Discover.Audio");
            if (!IsSameSiteOrDirect(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var token = request.Cookies["drive_token"];
            if (string.IsNullOrWhiteSpace(token) || await auth.IdentifyAsync(token, ct) is null)
                return Results.StatusCode(StatusCodes.Status401Unauthorized);

            if (BandcampUrls.ReleasePage(release) is not { } page)
                return Results.BadRequest();

            string? stream;
            try { stream = await bandcamp.StreamUrlAsync(page, trackId, ct); }
            catch (FetchCapException) { return Results.StatusCode(StatusCodes.Status429TooManyRequests); }
            catch (FetchFailedException e) { return Results.StatusCode(e.Status == 404 ? 404 : 502); }
            catch (HttpRequestException) { return Results.StatusCode(StatusCodes.Status502BadGateway); }

            if (stream is null)
            {
                log.LogInformation("No stream for track {TrackId} on {Path}", trackId, page.AbsolutePath);
                return Results.NotFound();
            }

            response.Headers[HeaderNames.CacheControl] = "no-store";
            if (probe == "1") return Results.Ok();
            // The target carries a signed token: logged never, cached never.
            return Results.Redirect(stream, permanent: false);
        });
    }

    private static ReleaseDto ToDto(BandcampRelease r) => new(
        "bandcamp", r.Url, r.Type, r.Id, r.Title, r.Artist, r.Label, r.LabelUrl, r.ReleaseDate, r.Tags,
        r.Tracks.Select(t => new TrackDto(t.TrackId, t.Number, t.Title, t.Artist, t.Duration, t.TrackUrl, t.Isrc,
            t.StreamUrl is not null)).ToList(),
        r.AlbumUrl, r.StreamingLimit);

    private static async Task<IResult> Fetch(Func<Task<IResult>> run)
    {
        try { return await run(); }
        catch (FetchCapException e) { return Results.Json(new { error = e.Message }, statusCode: 429); }
        catch (FetchFailedException e) when (e.Status == 404) { return Results.NotFound(new { error = "Bandcamp has no such page" }); }
        catch (FetchFailedException e) { return Results.Json(new { error = e.Message }, statusCode: 502); }
        catch (HttpRequestException) { return Results.Json(new { error = "Bandcamp did not answer" }, statusCode: 502); }
        catch (TaskCanceledException) { return Results.Json(new { error = "Bandcamp timed out" }, statusCode: 504); }
    }

    /// <summary>
    /// The allowlisted user only (ADR §9). Without this the service, which Cloud Run
    /// exposes to everyone, would be an open proxy fetching Bandcamp under our IPs.
    /// </summary>
    private static async Task<IResult?> Authorize(HttpRequest request, RemoteAuth auth, CancellationToken ct)
    {
        var header = request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token)) return Results.Unauthorized();
        return await auth.IdentifyAsync(token, ct) is null ? Results.StatusCode(StatusCodes.Status403Forbidden) : null;
    }

    /// <summary>Same rule as the Drive proxy: our pages, or no Origin/Referer at all.</summary>
    private static bool IsSameSiteOrDirect(HttpRequest request)
    {
        var value = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(value)) value = request.Headers.Referer.ToString();
        if (string.IsNullOrEmpty(value)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
               && string.Equals(uri.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase);
    }
}
