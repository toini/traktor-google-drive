namespace TraktorGoogleDrive.Server.Discover;

/// <summary>Thrown when the daily cap on outbound requests is spent.</summary>
public sealed class FetchCapException(string message) : Exception(message);

/// <summary>Thrown when the store answers with an error.</summary>
public sealed class FetchFailedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Every outbound request to one store goes through here (ADR 20261025-54,
/// decision 1): one at a time, at least <see cref="MinSpacing"/> apart, at most
/// <see cref="DailyCap"/> per UTC day, with an honest User-Agent. Callers wait
/// their turn rather than being refused; only the daily cap refuses.
/// </summary>
public sealed class PoliteFetcher
{
    public const string UserAgent = "traktor-google-drive/1.0 (+https://github.com/toini/traktor-google-drive)";

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _log;
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private DateOnly _day;
    private int _today;

    public TimeSpan MinSpacing { get; }
    public int DailyCap { get; }

    public PoliteFetcher(HttpClient http, TimeProvider clock, ILogger log, TimeSpan minSpacing, int dailyCap,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _clock = clock;
        _log = log;
        MinSpacing = minSpacing;
        DailyCap = dailyCap;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, clock, ct));
    }

    /// <summary>Requests made today (UTC). For the status endpoint and tests.</summary>
    public int UsedToday
    {
        get
        {
            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            return today == _day ? _today : 0;
        }
    }

    public async Task<string> GetStringAsync(Uri url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock.GetUtcNow();
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            if (today != _day) { _day = today; _today = 0; }
            if (_today >= DailyCap)
                throw new FetchCapException($"Daily cap of {DailyCap} requests to {url.Host} reached");

            var wait = _last + MinSpacing - now;
            if (wait > TimeSpan.Zero) await _delay(wait, ct);

            _today++;
            _last = _clock.GetUtcNow();

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "text/html");
            using var response = await _http.SendAsync(request, ct);

            // Path only: these are public catalogue pages, but nothing else belongs in a log.
            _log.LogInformation("Fetched {Host}{Path}: {Status} ({Count} today)",
                url.Host, url.AbsolutePath, (int)response.StatusCode, _today);

            if (!response.IsSuccessStatusCode)
                throw new FetchFailedException((int)response.StatusCode, $"{url.Host} answered {(int)response.StatusCode}");
            return await response.Content.ReadAsStringAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
