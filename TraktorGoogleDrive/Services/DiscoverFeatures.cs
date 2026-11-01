using Microsoft.JSInterop;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Discover features that are off unless switched on in this browser. SoundCloud
/// (ADR 20261025-54 §13.4) is one: Toni buys on Bandcamp or Beatport, and SoundCloud
/// sells nothing, so its finding, its widget and its links stay hidden. The code
/// and the data it wrote stay intact; to bring it back in a browser:
/// <c>localStorage.setItem("discover.soundcloud", "on")</c> and reload.
/// </summary>
public sealed class DiscoverFeatures(IJSRuntime js)
{
    public const string SoundCloudKey = "discover.soundcloud";

    private Task? _loading;

    /// <summary>SoundCloud in crates: "Try SoundCloud", its search link, its widget, SoundCloud URLs in the paste box.</summary>
    public bool SoundCloud { get; set; }

    public Task EnsureLoadedAsync() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        try { SoundCloud = await js.InvokeAsync<string?>("localStorage.getItem", SoundCloudKey) == "on"; }
        catch (JSException) { SoundCloud = false; }
    }
}
