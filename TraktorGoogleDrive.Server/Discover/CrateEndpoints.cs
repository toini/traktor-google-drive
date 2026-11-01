using TraktorGoogleDrive.Server.Remote;

namespace TraktorGoogleDrive.Server.Discover;

/// <summary>
/// Finding the tracks of a crate (ADR 20261025-54, "Crates"): a Discogs lookup by
/// artist and title, and a SoundCloud oEmbed check. Like the Bandcamp endpoints,
/// for the allowlisted user only, each call the answer to something Toni did.
/// Bandcamp's own <c>/search</c> is never called: the client opens it in a tab.
/// </summary>
public static class CrateEndpoints
{
    public static IServiceCollection AddDiscoverCrates(this IServiceCollection services)
    {
        services.AddHttpClient("discogs", c => c.Timeout = TimeSpan.FromSeconds(20))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });
        services.AddHttpClient("soundcloud", c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            });

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            // Discogs allows 25 unauthenticated requests a minute: one per 2.6 s stays under it.
            // Config can only make it stricter.
            var spacing = TimeSpan.FromSeconds(Math.Max(2.6, config.GetValue("Discover:Discogs:MinSpacingSeconds", 2.6)));
            var cap = Math.Clamp(config.GetValue("Discover:Discogs:DailyCap", 1000), 1, 1000);
            var token = config["Discover:DiscogsToken"];
            return new DiscogsSource(
                new PoliteFetcher(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("discogs"),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Discover.Discogs"),
                    spacing, cap, accept: "application/vnd.discogs.v2.discogs+json",
                    authorization: string.IsNullOrWhiteSpace(token) ? null : $"Discogs token={token.Trim()}"),
                sp.GetRequiredService<TimeProvider>());
        });

        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var spacing = TimeSpan.FromSeconds(Math.Max(2, config.GetValue("Discover:SoundCloud:MinSpacingSeconds", 2.0)));
            var cap = Math.Clamp(config.GetValue("Discover:SoundCloud:DailyCap", 300), 1, 300);
            return new SoundCloudSource(
                new PoliteFetcher(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("soundcloud"),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Discover.SoundCloud"),
                    spacing, cap, accept: "application/json"),
                sp.GetRequiredService<TimeProvider>());
        });
        return services;
    }

    public static void MapDiscoverCrates(this WebApplication app)
    {
        var api = app.MapGroup("/api/discover");

        api.MapGet("/discogs/lookup", async (HttpRequest request, string? artist, string? title, long? release,
            RemoteAuth auth, DiscogsSource discogs, CancellationToken ct) =>
        {
            if (await DiscoverEndpoints.Authorize(request, auth, ct) is { } refused) return refused;
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) || artist.Length > 300 || title.Length > 300)
                return Results.BadRequest(new { error = "Artist and title, please" });
            return await Fetch("Discogs", async () => Results.Ok(await discogs.LookupAsync(artist.Trim(), title.Trim(), release, ct)));
        });

        api.MapGet("/soundcloud/find", async (HttpRequest request, string? url, string? artist, string? title, string? uploader,
            RemoteAuth auth, SoundCloudSource soundcloud, CancellationToken ct) =>
        {
            if (await DiscoverEndpoints.Authorize(request, auth, ct) is { } refused) return refused;
            if (url is not null)
            {
                if (SoundCloudUrls.TrackPage(url) is not { } page)
                    return Results.BadRequest(new { error = "Not a SoundCloud track URL (soundcloud.com/<artist>/<track>)" });
                return await Fetch("SoundCloud", async () => await soundcloud.CheckAsync(page, ct) is { } track
                    ? Results.Ok(track)
                    : Results.NotFound(new { error = "SoundCloud has no public track at that URL" }));
            }
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title) || artist.Length > 300 || title.Length > 300)
                return Results.BadRequest(new { error = "Artist and title, please" });
            return await Fetch("SoundCloud", async () => await soundcloud.GuessAsync(artist, title, uploader, ct) is { } found
                ? Results.Ok(found)
                : Results.NotFound(new { error = "Not at the uploader's or the artist's SoundCloud under that title" }));
        });
    }

    private static async Task<IResult> Fetch(string store, Func<Task<IResult>> run)
    {
        try { return await run(); }
        catch (FetchCapException e) { return Results.Json(new { error = e.Message }, statusCode: 429); }
        catch (FetchFailedException e) when (e.Status == 429) { return Results.Json(new { error = $"{store} asked us to slow down" }, statusCode: 429); }
        catch (FetchFailedException e) { return Results.Json(new { error = e.Message }, statusCode: 502); }
        catch (HttpRequestException) { return Results.Json(new { error = $"{store} did not answer" }, statusCode: 502); }
        catch (TaskCanceledException) { return Results.Json(new { error = $"{store} timed out" }, statusCode: 504); }
        catch (System.Text.Json.JsonException) { return Results.Json(new { error = $"{store} sent something unreadable" }, statusCode: 502); }
    }
}
