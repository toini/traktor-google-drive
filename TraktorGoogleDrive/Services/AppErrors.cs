namespace TraktorGoogleDrive.Services;

public record AppError(string Summary, string? Detail, DateTimeOffset At)
{
    public string Time => At.ToLocalTime().ToString("HH:mm:ss");
}

/// <summary>
/// Collects failures so they reach the screen. Everything used to go to
/// Console.WriteLine, which in WASM only reaches the browser devtools — so a
/// broken collection load looked like an empty sidebar and nothing else.
/// </summary>
public class AppErrors
{
    private const int MaxRetained = 25;
    private readonly List<AppError> _errors = [];

    public IReadOnlyList<AppError> Errors => _errors;
    public bool Any => _errors.Count > 0;

    public event Action? Changed;

    public void Report(string summary, Exception? ex = null) =>
        Add(summary, ex is null ? null : Describe(ex));

    public void Report(string summary, string? detail) => Add(summary, detail);

    private void Add(string summary, string? detail)
    {
        // Deliberately stdout, not stderr: Blazor WASM treats any .NET write to
        // Console.Error as an unhandled-error signal and shows the framework's
        // "An unhandled error has occurred" bar, which is misleading for a
        // failure we have already caught and are rendering ourselves.
        Console.WriteLine($"[error] {summary}{(detail is null ? "" : $" :: {detail}")}");

        _errors.Insert(0, new AppError(summary, detail, DateTimeOffset.Now));
        if (_errors.Count > MaxRetained) _errors.RemoveRange(MaxRetained, _errors.Count - MaxRetained);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_errors.Count == 0) return;
        _errors.Clear();
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
