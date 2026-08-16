using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace TraktorGoogleDrive.Services;

public class AuthService
{
    private const string TokenKey = "access_token";

    private readonly IJSRuntime _js;
    private readonly NavigationManager _nav;
    private readonly AppErrors _errors;

    public AuthService(IJSRuntime js, NavigationManager nav, AppErrors errors)
    {
        _js = js;
        _nav = nav;
        _errors = errors;
    }

    /// <summary>
    /// Current token, or null if absent or expired.
    /// </summary>
    /// <remarks>
    /// Falls back to reading sessionStorage directly if auth.js is missing its
    /// helpers. A browser holding a stale cached auth.js against newer WASM used
    /// to throw here during App.OnInitializedAsync, which left the whole app
    /// dead on "Checking authentication..." with only a console stack trace.
    /// Degrading is always better than bricking the shell.
    /// </remarks>
    public async ValueTask<string?> GetTokenAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("authGetToken");
        }
        catch (JSException ex)
        {
            _errors.Report("auth.js is out of date — reload the page (Cmd+Shift+R) if this persists", ex);
            return await RawTokenAsync();
        }
    }

    public async Task<string?> SignInAsync()
    {
        try
        {
            return await _js.InvokeAsync<string?>("googleLogin");
        }
        catch (JSException ex)
        {
            _errors.Report("Could not start Google sign-in — auth.js failed to load", ex);
            return null;
        }
    }

    public async Task SignOutAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("authSignOut");
        }
        catch (JSException)
        {
            await _js.InvokeVoidAsync("sessionStorage.removeItem", TokenKey);
        }
    }

    private async ValueTask<string?> RawTokenAsync()
    {
        try { return await _js.InvokeAsync<string?>("sessionStorage.getItem", TokenKey); }
        catch { return null; }
    }

    /// <summary>
    /// Clears the dead token and sends the user back to sign in. Called when
    /// Drive rejects a token that had not yet reached its stored expiry.
    /// </summary>
    public async Task HandleExpiredAsync(string? returnTo = null)
    {
        await SignOutAsync();
        var target = string.IsNullOrEmpty(returnTo)
            ? "/login?expired=1"
            : $"/login?expired=1&returnTo={Uri.EscapeDataString(returnTo)}";
        _nav.NavigateTo(target, forceLoad: false);
    }
}
