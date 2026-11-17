using System.Diagnostics;

namespace DbxLight.Core;

// The consuming thread marks the stop; speculative tasks only check the deadline.
internal sealed class AgentTraversalBudget(double? seconds)
{
    internal static AsyncLocal<Func<double>?> ElapsedTestClock { get; } = new();
    internal static AsyncLocal<Action?> StopCheckTestCheckpoint { get; } = new();
    private readonly Func<double>? testClock = ElapsedTestClock.Value;
    private readonly Action? stopCheckpoint = StopCheckTestCheckpoint.Value;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    internal bool Expired => seconds is { } limit && (testClock?.Invoke() ?? elapsed.Elapsed.TotalSeconds) >= limit;
    internal bool TimeLimited { get; private set; }
    internal bool StopIfExpired()
    {
        stopCheckpoint?.Invoke();
        if (!Expired) return false;
        TimeLimited = true;
        return true;
    }

    internal static void Validate(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < 1 or > 3600)
            throw new ArgumentException("--time-limit must be 1..3600 seconds.");
    }
}
