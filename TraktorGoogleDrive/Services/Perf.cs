using System.Diagnostics;

namespace TraktorGoogleDrive.Services;

/// <summary>
/// Step timings on the console, as "[perf] step 123ms (detail)". Cheap enough to
/// leave on: one line per step, and the e2e performance test reads them to say
/// which step a regression is in.
/// </summary>
public static class Perf
{
    public static void Log(string step, Stopwatch watch, string? detail = null) =>
        Log(step, watch.Elapsed.TotalMilliseconds, detail);

    public static void Log(string step, double milliseconds, string? detail = null) =>
        Console.WriteLine($"[perf] {step} {milliseconds:0}ms" + (detail is null ? "" : $" ({detail})"));
}
