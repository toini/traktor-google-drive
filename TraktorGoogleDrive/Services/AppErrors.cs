namespace TraktorGoogleDrive.Services;

public enum Severity
{
    Info,
    Error,
}

public record AppError(string Summary, string? Detail, DateTimeOffset At, Severity Severity = Severity.Error)
{
    public string Time => At.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>
/// Collects notices so they reach the screen. Everything used to go to
/// Console.WriteLine, which in WASM only reaches the browser devtools — so a
/// broken collection load looked like an empty sidebar and nothing else.
/// </summary>
public class AppErrors
{
    private const int MaxRetained = 25;
    private readonly List<AppError> _entries = [];

    public IReadOnlyList<AppError> Errors => _entries;
    public bool Any => _entries.Count > 0;
    public bool AnyError => _entries.Any(e => e.Severity == Severity.Error);

    public event Action? Changed;

    public void Report(string summary, Exception? ex = null) =>
        Add(summary, ex is null ? null : Describe(ex), Severity.Error);

    public void Report(string summary, string? detail) => Add(summary, detail, Severity.Error);

    public void Info(string summary, string? detail = null) => Add(summary, detail, Severity.Info);

    private void Add(string summary, string? detail, Severity severity)
    {
        // Deliberately stdout, not stderr: Blazor WASM treats any .NET write to
        // Console.Error as an unhandled-error signal and shows the framework's
        // "An unhandled error has occurred" bar, which is misleading for a
        // notice we are already rendering ourselves.
        Console.WriteLine($"[{severity.ToString().ToLowerInvariant()}] {summary}"
                        + (detail is null ? "" : $" :: {detail}"));

        _entries.Insert(0, new AppError(summary, detail, DateTimeOffset.Now, severity));
        if (_entries.Count > MaxRetained) _entries.RemoveRange(MaxRetained, _entries.Count - MaxRetained);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        Changed?.Invoke();
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join("  <-  ", parts);
    }
}
