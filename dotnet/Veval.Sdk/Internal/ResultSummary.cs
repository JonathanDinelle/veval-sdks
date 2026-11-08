namespace Veval.Sdk.Internal;

/// <summary>
/// The one-line summaries on replay, item and scenario results, e.g.
/// <c>passed — 40/64 recorded steps used, 3 diverged, 12 live, stayed on path</c>.
/// </summary>
internal static class ResultSummary
{
    public static string ForReplay(IReadOnlyCollection<string> failures, ReplayStop? stop, ReplayFidelity? fidelity)
    {
        var head = stop != null
            ? $"inconclusive at step {stop.Step} '{stop.StepName}'"
            : failures.Count == 0 ? "passed" : $"failed ({Count(failures.Count, "failure")})";
        if (fidelity == null) return head;

        var used = fidelity.Bindings.Where(b => b.RecordedIndex != null).Select(b => b.RecordedIndex).Distinct().Count();
        var recorded = used + fidelity.Unused.Count;
        return $"{head} — {used}/{recorded} recorded steps used, {fidelity.Diverged} diverged, {fidelity.Live} live, " +
               (stop != null ? "left the path" : "stayed on path");
    }

    public static string ForScenario(ScenarioRunResult result)
    {
        var line = $"{(result.Passed ? "passed" : "failed")} — {result.PassCount} passed, {result.FailCount} failed, " +
                   $"{result.InconclusiveCount} inconclusive";
        if (result.InconclusiveCount > 0)
            line += result.InconclusiveRate > result.MaxInconclusiveRate
                ? $" ({Percent(result.InconclusiveRate)} > {Percent(result.MaxInconclusiveRate)} allowed)"
                : $" ({Percent(result.InconclusiveRate)}, within {Percent(result.MaxInconclusiveRate)} allowed)";
        var replayed = result.Results.Count(r => r.Fidelity != null);
        if (replayed > 0)
            line += $", {replayed - result.Results.Count(r => r.Fidelity != null && r.Inconclusive != null)}/{replayed} replays stayed on path";
        return line;
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Percent(double rate) => $"{(int)Math.Floor(rate * 100 + 0.5)}%";
}
