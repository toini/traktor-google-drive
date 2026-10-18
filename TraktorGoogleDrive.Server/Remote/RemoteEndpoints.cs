using System.Text.Json.Serialization;

using Microsoft.Net.Http.Headers;

namespace TraktorGoogleDrive.Server.Remote;

/// <summary>Wiring for remote control: services, the ticket endpoint and the hub.</summary>
public static class RemoteEndpoints
{
    public const string HubPath = "/api/remote/hub";
    public const string TicketPath = "/api/remote/ticket";

    public static IServiceCollection AddRemoteControl(this IServiceCollection services)
    {
        services.AddSingleton<RemoteAuth>();
        services.AddSingleton<RemoteRooms>();
        services.AddSignalR(options =>
            {
                // Default 32 KB is plenty: a snapshot is well under 1 KB.
                options.MaximumReceiveMessageSize = 32 * 1024;
            })
            .AddJsonProtocol(options =>
                // A field the protocol does not define is refused, not ignored,
                // so nothing extra (a token, say) can ride along unnoticed.
                options.PayloadSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
        return services;
    }

    public static void MapRemoteControl(this WebApplication app)
    {
        // A tab trades its Google token (in a header, never a URL) for a ticket.
        app.MapPost(TicketPath, async (HttpRequest request, RemoteAuth auth, CancellationToken ct) =>
        {
            if (!RemoteAuth.IsSameOrigin(request)) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var header = request.Headers[HeaderNames.Authorization].ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : "";
            var user = await auth.IdentifyAsync(token, ct);
            if (user is null) return Results.StatusCode(StatusCodes.Status401Unauthorized);

            return Results.Json(new { ticket = auth.IssueTicket(user) });
        });

        // The ticket is checked before the WebSocket upgrade, so a bad one is a
        // plain 401 rather than a connection the hub has to tear down.
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments(HubPath), branch => branch.Use(async (ctx, next) =>
        {
            if (!RemoteAuth.IsSameOrigin(ctx.Request))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            // SignalR's browser client sends its access token as this query
            // parameter on the WebSocket URL (a browser cannot set headers there);
            // other clients send it as a bearer header. Either way, here that
            // "token" is the ticket, never the Google token.
            var header = ctx.Request.Headers[HeaderNames.Authorization].ToString();
            var ticket = header.StartsWith("Bearer ", StringComparison.Ordinal)
                ? header["Bearer ".Length..].Trim()
                : ctx.Request.Query["access_token"].ToString();
            var userKey = ctx.RequestServices.GetRequiredService<RemoteAuth>().ValidateTicket(ticket);
            if (userKey is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            ctx.Items[RemoteHub.UserKeyItem] = userKey;
            await next();
        }));

        app.UseWebSockets();
        app.MapHub<RemoteHub>(HubPath, options =>
            // WebSockets only: one request, so one instance. Negotiate followed
            // by a separate connect could land on two different instances.
            options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets);
    }
}
