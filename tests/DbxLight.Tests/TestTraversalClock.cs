using DbxLight.Core;

namespace DbxLight.Tests;

internal sealed class TestTraversalClock : IDisposable
{
    private readonly Func<double>? previousClock = AgentTraversalBudget.ElapsedTestClock.Value;
    private readonly Action? previousCheckpoint = AgentTraversalBudget.StopCheckTestCheckpoint.Value;
    private readonly object gate = new();
    private double seconds;
    internal TestTraversalClock() => AgentTraversalBudget.ElapsedTestClock.Value = () => Seconds;
    internal double Seconds { get { lock (gate) return seconds; } }
    internal void Advance(double delta) { lock (gate) seconds += delta; }
    internal void Reset() { lock (gate) seconds = 0; }
    internal Action? StopCheckpoint { set => AgentTraversalBudget.StopCheckTestCheckpoint.Value = value; }
    public void Dispose()
    {
        AgentTraversalBudget.ElapsedTestClock.Value = previousClock;
        AgentTraversalBudget.StopCheckTestCheckpoint.Value = previousCheckpoint;
    }
}
