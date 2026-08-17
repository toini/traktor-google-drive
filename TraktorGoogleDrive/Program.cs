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
builder.Services.AddScoped<DriveService>();
builder.Services.AddScoped<CollectionService>();
builder.Services.AddScoped<SetMatcher>();
// Singleton so every component shares one audio element — two tracks playing
// at once is then not representable. The Cast session is shared for the same
// reason, and outlives navigation.
builder.Services.AddSingleton<CastService>();
builder.Services.AddSingleton<PlayerService>();

await builder.Build().RunAsync();
