using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.JSInterop;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Brings the last track back after a reload: loaded in the player bar, paused
/// at the position it was at. It only observes <see cref="PlayerService"/>, so
/// the player itself needs no knowledge of persistence.
/// </summary>
/// <remarks>
/// Stored in localStorage under <c>playback:v1</c>. The access token is never
/// stored: the media URL embeds it, so the URL is rebuilt from a fresh token on
/// restore rather than saved. The position is kept current by remember.js
/// (throttled to 5 s, plus pause, seek, ended and pagehide).
/// </remarks>
public sealed class PlaybackMemory : IAsyncDisposable
{
    private const int Version = 1;
    private static readonly TimeSpan CastSaveInterval = TimeSpan.FromSeconds(5);

    private readonly IJSRuntime _js;
    private readonly PlayerService _player;
    private readonly AuthService _auth;
    private readonly DriveService _drive;
    private IJSObjectReference? _module;

    private string? _rememberedId;
    private bool _restoreDone;
    private DateTime _lastCastSave = DateTime.MinValue;

    public PlaybackMemory(IJSRuntime js, PlayerService player, AuthService auth, DriveService drive)
    {
        _js = js;
        _player = player;
        _auth = auth;
        _drive = drive;
        _player.Changed += OnPlayerChanged;
    }

    internal sealed record Snapshot(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("fileId")] string FileId,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("isWav")] bool IsWav,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("position")] double Position,
        [property: JsonPropertyName("savedAt")] long SavedAt);

    private async Task<IJSObjectReference> ModuleAsync() =>
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./remember.js");

    private void OnPlayerChanged()
    {
        var id = _player.CurrentFileId;

        if (id is null)
        {
            // Stopped (e.g. switched playlist): nothing was playing, so a reload
            // should bring nothing back. A Cast session without media also
            // reads as null, and must not wipe the local track's record.
            if (_rememberedId is not null && !_player.IsCasting)
            {
                _rememberedId = null;
                _ = RunAsync(m => m.InvokeVoidAsync("forgetPlayback").AsTask());
            }
            return;
        }

        if (id != _rememberedId)
        {
            _rememberedId = id;
            var snapshot = new Snapshot(Version, id, _player.CurrentTitle, _player.CurrentIsWav,
                _player.Duration, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _ = RunAsync(async m =>
            {
                var element = await _player.MediaElementAsync();
                await m.InvokeVoidAsync("rememberPlayback", JsonSerializer.Serialize(snapshot), element, false);
            });
            return;
        }

        // The TV's position never reaches the local element, so save it from here.
        if (_player.IsCasting && DateTime.UtcNow - _lastCastSave >= CastSaveInterval)
        {
            _lastCastSave = DateTime.UtcNow;
            var position = _player.CurrentTime;
            _ = RunAsync(m => m.InvokeVoidAsync("savePlaybackPosition", position).AsTask());
        }
    }

    /// <summary>
    /// Cues the remembered track, once per app lifetime. Safe to call on every
    /// render of the player bar. Never plays: browsers block autoplay without a
    /// gesture, and surprise audio after a reload is worse than none.
    /// </summary>
    public async Task RestoreAsync()
    {
        if (_restoreDone || _player.CurrentFileId is not null) return;

        Snapshot? snapshot;
        try
        {
            var module = await ModuleAsync();
            snapshot = Parse(await module.InvokeAsync<string?>("readPlayback"));
            if (snapshot is null)
            {
                _restoreDone = true;
                await module.InvokeVoidAsync("forgetPlayback");
                return;
            }
        }
        catch (Exception ex)
        {
            _restoreDone = true;
            Console.WriteLine($"[PlaybackMemory] storage unavailable, not restoring: {ex.Message}");
            return;
        }

        // No token yet: the app is on its way to the sign-in page. Keep the record
        // and try again when the player bar renders after sign-in.
        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrWhiteSpace(token)) return;

        try
        {
            // One small metadata call, so a file deleted or renamed away since the
            // last session is dropped quietly rather than surfacing as a playback
            // error on a page the user has not touched yet.
            if (await _drive.GetMetadataAsync(snapshot.FileId, token) is null)
            {
                _restoreDone = true;
                Console.WriteLine($"[PlaybackMemory] {snapshot.FileId} is gone from Drive; forgetting it");
                await RunAsync(m => m.InvokeVoidAsync("forgetPlayback").AsTask());
                return;
            }
        }
        catch (DriveAuthException)
        {
            // Token rejected: the page's own load handles sign-in. Keep the record.
            return;
        }

        _restoreDone = true;
        if (_player.CurrentFileId is not null) return; // user started something meanwhile

        // Set before cueing, or the Changed event would re-remember it at 0:00.
        _rememberedId = snapshot.FileId;
        await RunAsync(async m =>
        {
            var element = await _player.MediaElementAsync();
            await m.InvokeVoidAsync("rememberPlayback", JsonSerializer.Serialize(snapshot), element, true);
        });
        await _player.CueAsync(snapshot.FileId, snapshot.Title, snapshot.IsWav,
            snapshot.Duration, snapshot.Position, token);
    }

    internal static Snapshot? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<Snapshot>(raw);
            if (s is null || s.V != Version || string.IsNullOrWhiteSpace(s.FileId)) return null;
            var duration = double.IsFinite(s.Duration) && s.Duration > 0 ? s.Duration : 0;
            var position = double.IsFinite(s.Position) && s.Position > 0 ? s.Position : 0;
            if (duration > 0 && position >= duration) position = 0; // finished: start over
            return s with { Duration = duration, Position = position };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task RunAsync(Func<IJSObjectReference, Task> action)
    {
        try
        {
            await action(await ModuleAsync());
        }
        catch (Exception ex)
        {
            // Remembering is a convenience; it must never break playback.
            Console.WriteLine($"[PlaybackMemory] {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _player.Changed -= OnPlayerChanged;
        if (_module is not null)
        {
            try { await _module.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
    }
}
