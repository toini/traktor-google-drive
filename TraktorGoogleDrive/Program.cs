using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

using TraktorGoogleDrive;
using TraktorGoogleDrive.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
// Singleton so a failure raised on one page is still visible after navigating.
builder.Services.AddSingleton<AppErrors>();
builder.Services.AddScoped<AuthService>();
// IndexedDB, for what must survive a reload: collection.nml and the Drive file index.
builder.Services.AddScoped<LocalCache>();
builder.Services.AddScoped<DriveService>();
builder.Services.AddScoped<CollectionService>();
builder.Services.AddScoped<SetMatcher>();
// Singleton so every component shares one audio element — two tracks playing
// at once is then not representable. The Cast session is shared for the same
// reason, and outlives navigation.
builder.Services.AddSingleton<CastService>();
// The remote-control room (ADR 20261018-63): one connection per tab, shared by
// the player bar and the bridge that publishes this tab's playback.
builder.Services.AddSingleton<RemoteService>();
builder.Services.AddSingleton<PlayerService>();
builder.Services.AddSingleton<RemoteBridge>();
// Scoped only because it reads the token and Drive, which are; WASM has one scope.
builder.Services.AddScoped<PlaybackMemory>();

await builder.Build().RunAsync();
