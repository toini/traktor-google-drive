using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace TraktorGoogleDrive.Services;

public class AuthService
{
    private readonly IJSRuntime _js;
    private readonly NavigationManager _nav;

    public AuthService(IJSRuntime js, NavigationManager nav)
    {
        _js = js;
        _nav = nav;
    }

    /// <summary>Current token, or null if absent or expired.</summary>
    public ValueTask<string?> GetTokenAsync() => _js.InvokeAsync<string?>("authGetToken");

    public async Task<string?> SignInAsync() => await _js.InvokeAsync<string?>("googleLogin");

    public async Task SignOutAsync() => await _js.InvokeVoidAsync("authSignOut");

    /// <summary>
    /// Clears the dead token and sends the user back to sign in. Called when
    /// Drive rejects a token that had not yet reached its stored expiry.
    /// </summary>
    public async Task HandleExpiredAsync(string? returnTo = null)
    {
        await SignOutAsync();
        var target = string.IsNullOrEmpty(returnTo) ? "/login?expired=1" : $"/login?expired=1&returnTo={Uri.EscapeDataString(returnTo)}";
        _nav.NavigateTo(target, forceLoad: false);
    }
}
