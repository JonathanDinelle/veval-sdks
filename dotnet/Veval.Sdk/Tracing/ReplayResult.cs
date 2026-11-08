namespace Veval.Sdk;

/// <summary>How a replay ended.</summary>
public enum ReplayOutcome
{
    Passed,
    Failed,
    /// <summary>A live replay left its recording and stopped; see <see cref="ReplayResult.Inconclusive"/>.</summary>
    Inconclusive,
}

public class ReplayResult
{
    /// <summary>True only when the replay passed; false when it failed or was inconclusive.</summary>
    public bool Passed => Outcome == ReplayOutcome.Passed;
    public ReplayOutcome Outcome =>
        Inconclusive != null ? ReplayOutcome.Inconclusive : Failures.Count == 0 ? ReplayOutcome.Passed : ReplayOutcome.Failed;
    /// <summary>
    /// Where a live replay stopped because a call left the recording, or null. When set, assertions were not evaluated
    /// and <see cref="Failures"/> is empty: the replay can't back up a pass or a fail.
    /// </summary>
    public ReplayStop? Inconclusive { get; set; }
    public List<string> Failures { get; set; } = new();
    public VevalExecutionContext? ReplayedContext { get; set; }
    public object? Output { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    /// <summary>The replay trace's status: "success", "error" (the agent threw), or "inconclusive" (the replay stopped).</summary>
    public string Status { get; set; } = "success";
    public string? Error { get; set; }

    /// <summary>How the replay differed from its recording; set when <see cref="ReplayOptions.CompareWithRecording"/> is on.</summary>
    public SnapshotDiff? RecordingDiff { get; set; }

    /// <summary>
    /// How each call was bound to the recording; set for mocked and live replays
    /// (<see cref="ReplayOptions.MockLlmResponses"/> or <see cref="ReplayOptions.LiveModelSteps"/>).
    /// </summary>
    public ReplayFidelity? Fidelity { get; set; }

    /// <summary>One line, e.g. <c>passed — 40/64 recorded steps used, 3 diverged but served, 12 live, stayed on path</c>.</summary>
    public string Summary => Internal.ResultSummary.ForReplay(Failures, Inconclusive, Fidelity);
}
