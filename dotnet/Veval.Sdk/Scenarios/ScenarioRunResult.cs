using Veval.Sdk.Internal;

namespace Veval.Sdk;

public class ScenarioRunResult
{
    /// <summary>
    /// True when no item failed, no item stopped after an injected fault, and the share of other inconclusive items is
    /// within <see cref="MaxInconclusiveRate"/> (by default, none may be inconclusive).
    /// </summary>
    public bool Passed => FailCount == 0 && InconclusiveAfterFaultCount == 0 && InconclusiveRate <= MaxInconclusiveRate;
    public int PassCount => Results.Count(r => r.Passed);
    /// <summary>Items that failed. Inconclusive items are counted in <see cref="InconclusiveCount"/>, not here.</summary>
    public int FailCount => Results.Count(r => !r.Passed && r.Inconclusive == null);
    /// <summary>Items whose live replay left its recording and stopped, so they neither passed nor failed.</summary>
    public int InconclusiveCount => Results.Count(r => r.Inconclusive != null);
    /// <summary>
    /// Inconclusive items that stopped after an injected fault (<see cref="ReplayStopReason.AfterFault"/>), included in
    /// <see cref="InconclusiveCount"/>. They are left out of <see cref="InconclusiveRate"/> and always fail the run: the
    /// fault test couldn't be judged, which no allowance should hide.
    /// </summary>
    public int InconclusiveAfterFaultCount => Results.Count(r => r.InconclusiveAfterFault);
    /// <summary>
    /// Inconclusive items as a share of replayed items, from 0 to 1, leaving out items that stopped after an injected
    /// fault. Only a replay can be inconclusive, so synthetic items don't dilute it.
    /// </summary>
    public double InconclusiveRate
    {
        get
        {
            var replayed = Results.Count(r => r.Replayed && !r.InconclusiveAfterFault);
            return replayed == 0 ? 0 : (double)(InconclusiveCount - InconclusiveAfterFaultCount) / replayed;
        }
    }
    /// <summary>The <see cref="ScenarioOptions.MaxInconclusiveRate"/> the run was judged against.</summary>
    public double MaxInconclusiveRate { get; set; }
    /// <summary>
    /// Replayed items that stayed on their recorded path (didn't stop), as a share of replayed items; null when no
    /// item was replayed. How far live replay can be trusted for this change. Items that stopped after an injected
    /// fault are left out: the fault sent them off the path.
    /// </summary>
    public double? StayedOnPathRate
    {
        get
        {
            var replayed = Results.Count(r => r.Replayed && !r.InconclusiveAfterFault);
            return replayed == 0 ? null : (double)(replayed - (InconclusiveCount - InconclusiveAfterFaultCount)) / replayed;
        }
    }
    public List<ItemRunResult> Results { get; set; } = new();

    /// <summary>One line, e.g. <c>failed: too many inconclusive — 5 passed, 0 failed, 1 inconclusive (17% of replays > 0% allowed), 5/6 replays stayed on path</c>.</summary>
    public string Summary => ResultSummary.ForScenario(this);
}
