using System.Text;

using Microsoft.JSInterop;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// What survives a reload: UTF-8 bytes in IndexedDB (idb-cache.js). Purely an
/// optimisation, so it never throws. A miss, or storage that is unavailable,
/// means the caller fetches from Drive as if there were no cache at all.
/// </summary>
public class LocalCache
{
    private readonly IJSRuntime _js;
    private Task<IJSObjectReference>? _module;

    public LocalCache(IJSRuntime js) => _js = js;

    private Task<IJSObjectReference> Module() =>
        _module ??= _js.InvokeAsync<IJSObjectReference>("import", "./idb-cache.js").AsTask();

    public async Task<byte[]?> GetBytesAsync(string key)
    {
        try { return await (await Module()).InvokeAsync<byte[]?>("get", key); }
        catch { _module = null; return null; }
    }

    public async Task<string?> GetAsync(string key) =>
        await GetBytesAsync(key) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>False when it was not stored (quota, private window, no IndexedDB).</summary>
    public async Task<bool> SetBytesAsync(string key, byte[] value)
    {
        try { return await (await Module()).InvokeAsync<bool>("set", key, value); }
        catch { _module = null; return false; }
    }

    public Task<bool> SetAsync(string key, string value) => SetBytesAsync(key, Encoding.UTF8.GetBytes(value));

    public async Task RemoveAsync(string key)
    {
        try { await (await Module()).InvokeAsync<bool>("remove", key); }
        catch { _module = null; }
    }
}
