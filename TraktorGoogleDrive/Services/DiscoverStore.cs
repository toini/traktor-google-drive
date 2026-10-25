using System.Text;
using System.Text.Json;

using Microsoft.JSInterop;

using TraktorGoogleDrive.Discover;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Discover's durable state (ADR 20261025-54 §8, S-A): one append-only event log
/// per device per month in the Drive appDataFolder,
/// <c>discover-&lt;device&gt;-&lt;yyyy-MM&gt;.jsonl</c>. A device only ever writes its
/// own files, so the tablet and the laptop cannot overwrite each other's
/// decisions. Unsaved events also wait in localStorage, so a missing scope, a
/// dropped connection or a closed tab loses nothing.
/// </summary>
public sealed class DiscoverStore
{
    public const string FilePrefix = "discover-";
    private const string DeviceKey = "discover:device:v1";
    private const string PendingKey = "discover:pending:v1";
    private static readonly TimeSpan FlushDelay = TimeSpan.FromSeconds(1.5);

    public enum SaveStatus { Loading, Saved, Saving, Pending, NeedsConsent, Failed }

    private readonly AppDataStore _drive;
    private readonly AuthService _auth;
    private readonly IJSRuntime _js;
    private readonly Func<DateTimeOffset> _now;

    private readonly List<DiscoverEvent> _own = [];         // this device, current month: the file's whole content
    private readonly List<DiscoverEvent> _pending = [];     // not yet in Drive
    private readonly Dictionary<string, string> _fileIds = new();
    private string _device = "";
    private long _seq;
    private CancellationTokenSource? _flushDelay;
    private Task _flushing = Task.CompletedTask;

    public DiscoverState State { get; private set; } = new();
    public SaveStatus Status { get; private set; } = SaveStatus.Loading;
    public string? StatusDetail { get; private set; }
    public event Action? Changed;

    public DiscoverStore(AppDataStore drive, AuthService auth, IJSRuntime js, Func<DateTimeOffset>? now = null)
    {
        _drive = drive;
        _auth = auth;
        _js = js;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private string MonthFile(DateTimeOffset at) => $"{FilePrefix}{_device}-{at:yyyy-MM}.jsonl";

    public async Task LoadAsync()
    {
        _device = await LocalGet(DeviceKey) ?? "";
        if (_device.Length == 0)
        {
            _device = Convert.ToHexString(Guid.NewGuid().ToByteArray())[..10].ToLowerInvariant();
            await LocalSet(DeviceKey, _device);
        }

        var all = new List<DiscoverEvent>();
        var pendingJson = await LocalGet(PendingKey);
        if (!string.IsNullOrEmpty(pendingJson))
        {
            try { _pending.AddRange(JsonSerializer.Deserialize<List<DiscoverEvent>>(pendingJson, DiscoverState.Json) ?? []); }
            catch (JsonException) { /* a corrupt queue is dropped, not fatal */ }
        }

        try
        {
            var token = await _auth.GetTokenAsync() ?? throw new DriveAuthException("No access token");
            var currentFile = MonthFile(_now());
            foreach (var file in await _drive.ListAsync(FilePrefix, token))
            {
                _fileIds[file.Name] = file.Id;
                var events = Parse(await _drive.ReadAsync(file.Id, token));
                all.AddRange(events);
                if (file.Name == currentFile) _own.AddRange(events);
            }
            Status = _pending.Count > 0 ? SaveStatus.Pending : SaveStatus.Saved;
            StatusDetail = null;
        }
        catch (AppDataScopeException e) { Status = SaveStatus.NeedsConsent; StatusDetail = e.Message; }
        catch (Exception e) when (e is DriveRequestException or DriveAuthException or HttpRequestException)
        {
            Status = SaveStatus.Failed;
            StatusDetail = e.Message;
        }

        var ownKeys = all.Where(e => e.Device == _device).Select(e => e.Seq).ToHashSet();
        _seq = all.Concat(_pending).Where(e => e.Device == _device).Select(e => e.Seq).DefaultIfEmpty(0).Max();
        // Pending events already in Drive (saved, but the tab closed before the queue was cleared) are not doubled.
        _pending.RemoveAll(e => ownKeys.Contains(e.Seq) && e.Device == _device);

        State = DiscoverState.Fold(all.Concat(_pending));
        Changed?.Invoke();
        if (_pending.Count > 0 && Status == SaveStatus.Pending) ScheduleFlush();
    }

    /// <summary>Records one event: applied at once, saved shortly after.</summary>
    public void Append(DiscoverEvent e)
    {
        var stamped = e with { At = _now(), Device = _device, Seq = ++_seq };
        State.Apply(stamped);
        _pending.Add(stamped);
        _ = LocalSet(PendingKey, JsonSerializer.Serialize(_pending, DiscoverState.Json));
        if (Status != SaveStatus.NeedsConsent) Status = SaveStatus.Pending;
        Changed?.Invoke();
        if (Status != SaveStatus.NeedsConsent) ScheduleFlush();
    }

    public void ScheduleFlush()
    {
        _flushDelay?.Cancel();
        var cts = _flushDelay = new CancellationTokenSource();
        _ = Task.Delay(FlushDelay, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = FlushAsync();
        }, TaskScheduler.Default);
    }

    /// <summary>Writes pending events to this device's current-month file.</summary>
    public Task FlushAsync()
    {
        _flushing = _flushing.ContinueWith(_ => FlushOnceAsync()).Unwrap();
        return _flushing;
    }

    private async Task FlushOnceAsync()
    {
        if (_pending.Count == 0) return;
        var batch = _pending.ToList();
        Status = SaveStatus.Saving;
        Changed?.Invoke();
        try
        {
            var token = await _auth.GetTokenAsync() ?? throw new DriveAuthException("No access token");
            foreach (var group in batch.GroupBy(e => MonthFile(e.At)))
            {
                var name = group.Key;
                var isCurrent = name == MonthFile(_now());
                // The current month's file is rewritten whole; Drive cannot append.
                var lines = isCurrent ? _own.Concat(group) : group;
                var content = Serialize(lines);
                if (_fileIds.TryGetValue(name, out var id)) await _drive.UpdateAsync(id, content, token);
                else _fileIds[name] = await _drive.CreateAsync(name, content, token);
                if (isCurrent) _own.AddRange(group);
            }
            _pending.RemoveAll(batch.Contains);
            await LocalSet(PendingKey, JsonSerializer.Serialize(_pending, DiscoverState.Json));
            Status = _pending.Count > 0 ? SaveStatus.Pending : SaveStatus.Saved;
            StatusDetail = null;
        }
        catch (AppDataScopeException e) { Status = SaveStatus.NeedsConsent; StatusDetail = e.Message; }
        catch (Exception e) when (e is DriveRequestException or DriveAuthException or HttpRequestException)
        {
            Status = SaveStatus.Failed;
            StatusDetail = e.Message;
        }
        Changed?.Invoke();
    }

    /// <summary>After Toni granted the scope: read Drive again and save what waited.</summary>
    public async Task RetryAsync()
    {
        _own.Clear();
        _fileIds.Clear();
        await LoadAsync();
        if (Status is SaveStatus.Pending or SaveStatus.Saved) await FlushAsync();
    }

    public static string Serialize(IEnumerable<DiscoverEvent> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events) sb.Append(JsonSerializer.Serialize(e, DiscoverState.Json)).Append('\n');
        return sb.ToString();
    }

    public static List<DiscoverEvent> Parse(string jsonl)
    {
        var list = new List<DiscoverEvent>();
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { if (JsonSerializer.Deserialize<DiscoverEvent>(line, DiscoverState.Json) is { } e) list.Add(e); }
            catch (JsonException) { /* one bad line costs one event, not the log */ }
        }
        return list;
    }

    private async Task<string?> LocalGet(string key)
    {
        try { return await _js.InvokeAsync<string?>("localStorage.getItem", key); }
        catch { return null; }
    }

    private async Task LocalSet(string key, string value)
    {
        try { await _js.InvokeVoidAsync("localStorage.setItem", key, value); }
        catch { /* storage blocked: Drive is still the record */ }
    }
}
