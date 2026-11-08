using Veval.Sdk.Internal;

namespace Veval.Sdk;

public class ItemRunResult
{
    public ScenarioItem Item { get; set; } = null!;
    /// <summary>True only when the item passed; false when it failed or was inconclusive.</summary>
    public bool Passed => Failures.Count == 0 && Inconclusive == null;
    /// <summary>How the item ended: passed, failed, or inconclusive (its live replay left the recording and stopped).</summary>
    public ReplayOutcome Outcome =>
        Inconclusive != null ? ReplayOutcome.Inconclusive : Failures.Count == 0 ? ReplayOutcome.Passed : ReplayOutcome.Failed;
    public List<string> Failures { get; set; } = new();
    /// <summary>Where the item's live replay left its recording and stopped, or null. When set, its assertions were not evaluated.</summary>
    public ReplayStop? Inconclusive { get; set; }
    /// <summary>How the item's replay was bound to its recording; null for a synthetic item or a missing trace.</summary>
    public ReplayFidelity? Fidelity { get; set; }
    public VevalExecutionContext? Context { get; set; }

    /// <summary>One line, e.g. <c>passed — 40/64 recorded steps used, 3 diverged, 12 live, stayed on path</c>.</summary>
    public string Summary => ResultSummary.ForReplay(Failures, Inconclusive, Fidelity);
}
