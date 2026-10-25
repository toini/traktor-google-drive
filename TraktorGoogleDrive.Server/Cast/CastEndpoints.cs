using Microsoft.Net.Http.Headers;

using TraktorGoogleDrive.Server.Remote;

namespace TraktorGoogleDrive.Server.Cast;

/// <summary>
/// The sender's side of Cast media tickets. Every call carries the Google token
/// in the Authorization header and the ticket and renew key in the body, so none
/// of them reaches a URL or the request log. Only the device's media URL carries
/// the ticket (see <c>/api/proxy/cast/{fileId}</c> in Program.cs).
/// </summary>
public static class CastEndpoints
{
    public const string TicketsPath = "/api/cast/tickets";

    public sealed record MintRequest(string? FileId, long? TokenExpiresAt);
    public sealed record RenewRequest(string? Ticket, string? RenewKey, long? TokenExpiresAt);

    public static IServiceCollection AddCastTickets(this IServiceCollection services) =>
        services.AddSingleton<CastTickets>();

    public static void MapCastTickets(this WebApplication app)
    {
        // Mint: one ticket per LOAD, scoped to that file.
        app.MapPost(TicketsPath, (HttpRequest request, MintRequest body, CastTickets tickets) =>
        {
            if (!RemoteAuth.IsSameOrigin(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (BearerToken(request) is not { } token) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            if (!CastTickets.IsValidFileId(body.FileId)) return Results.BadRequest();

            var issued = tickets.Issue(body.FileId!, token, FromUnixMs(body.TokenExpiresAt));
            return Results.Json(new
            {
                ticket = issued.Ticket,
                renewKey = issued.RenewKey,
                expiresAt = issued.ExpiresAt.ToUnixTimeMilliseconds(),
            });
        });

        // Renew: a fresh token behind the same ticket, so the device's URL is
        // unchanged. Also the heartbeat that restores a grant after a restart.
        app.MapPost(TicketsPath + "/renew", (HttpRequest request, RenewRequest body, CastTickets tickets) =>
        {
            if (!RemoteAuth.IsSameOrigin(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (BearerToken(request) is not { } token) return Results.StatusCode(StatusCodes.Status401Unauthorized);

            return tickets.Renew(body.Ticket, body.RenewKey, token, FromUnixMs(body.TokenExpiresAt)) switch
            {
                CastTickets.Verdict.Ok => Results.NoContent(),
                // Past its hard cap: the sender has to mint a new one and reload.
                CastTickets.Verdict.Expired => Results.StatusCode(StatusCodes.Status410Gone),
                _ => Results.StatusCode(StatusCodes.Status403Forbidden),
            };
        });

        // Revoke: the device moved on (stop, disconnect, next track).
        app.MapPost(TicketsPath + "/revoke", (HttpRequest request, RenewRequest body, CastTickets tickets) =>
        {
            if (!RemoteAuth.IsSameOrigin(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return tickets.Revoke(body.Ticket, body.RenewKey) == CastTickets.Verdict.Ok
                ? Results.NoContent()
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        });
    }

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers[HeaderNames.Authorization].ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        var token = header["Bearer ".Length..].Trim();
        return token.Length > 0 ? token : null;
    }

    private static DateTimeOffset? FromUnixMs(long? ms) =>
        ms is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms.Value) : null;
}
