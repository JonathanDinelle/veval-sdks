namespace Veval.Sdk.Internal;

/// <summary>
/// The one-line summaries on replay, item and scenario results, e.g.
/// <c>passed — 40/64 recorded steps used, 3 diverged but served, 12 live, stayed on path</c>.
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
        return $"{head} — {used}/{recorded} recorded steps used, {fidelity.Diverged} diverged but served, {fidelity.Live} live, " +
               (stop != null ? "left the path" : "stayed on path");
    }

    public static string ForScenario(ScenarioRunResult result)
    {
        var tooManyInconclusive = result.InconclusiveRate > result.MaxInconclusiveRate;
        // The reason comes first: "failed ... 0 failed" would otherwise read like a bug.
        var head = result.Passed ? "passed"
            : result.FailCount == 0 && tooManyInconclusive ? "failed: too many inconclusive"
            : "failed";
        var line = $"{head} — {result.PassCount} passed, {result.FailCount} failed, {result.InconclusiveCount} inconclusive";
        if (result.InconclusiveCount > 0)
            line += tooManyInconclusive
                ? $" ({Percent(result.InconclusiveRate)} of replays > {Percent(result.MaxInconclusiveRate)} allowed)"
                : $" ({Percent(result.InconclusiveRate)} of replays, within {Percent(result.MaxInconclusiveRate)} allowed)";
        var replayed = result.Results.Count(r => r.Replayed);
        if (replayed > 0)
            line += $", {replayed - result.InconclusiveCount}/{replayed} replays stayed on path";
        return line;
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Percent(double rate) => $"{(int)Math.Floor(rate * 100 + 0.5)}%";
}
