using Veval.Sdk.Internal;

namespace Veval.Sdk;

public class ScenarioRunResult
{
    /// <summary>
    /// True when no item failed and the share of inconclusive items is within <see cref="MaxInconclusiveRate"/>
    /// (by default, none may be inconclusive).
    /// </summary>
    public bool Passed => FailCount == 0 && InconclusiveRate <= MaxInconclusiveRate;
    public int PassCount => Results.Count(r => r.Passed);
    /// <summary>Items that failed. Inconclusive items are counted in <see cref="InconclusiveCount"/>, not here.</summary>
    public int FailCount => Results.Count(r => !r.Passed && r.Inconclusive == null);
    /// <summary>Items whose live replay left its recording and stopped, so they neither passed nor failed.</summary>
    public int InconclusiveCount => Results.Count(r => r.Inconclusive != null);
    /// <summary>Inconclusive items as a share of all items, from 0 to 1.</summary>
    public double InconclusiveRate => Results.Count == 0 ? 0 : (double)InconclusiveCount / Results.Count;
    /// <summary>The <see cref="ScenarioOptions.MaxInconclusiveRate"/> the run was judged against.</summary>
    public double MaxInconclusiveRate { get; set; }
    /// <summary>
    /// Replayed items that stayed on their recorded path (didn't stop), as a share of replayed items; null when no
    /// item was replayed. How far live replay can be trusted for this change.
    /// </summary>
    public double? StayedOnPathRate
    {
        get
        {
            var replayed = Results.Count(r => r.Fidelity != null);
            return replayed == 0 ? null : (double)Results.Count(r => r.Fidelity != null && r.Inconclusive == null) / replayed;
        }
    }
    public List<ItemRunResult> Results { get; set; } = new();

    /// <summary>One line, e.g. <c>failed — 5 passed, 0 failed, 1 inconclusive (17% > 0% allowed), 5/6 replays stayed on path</c>.</summary>
    public string Summary => ResultSummary.ForScenario(this);
}
