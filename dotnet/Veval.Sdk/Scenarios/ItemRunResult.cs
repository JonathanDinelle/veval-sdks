namespace Veval.Sdk;

public class ItemRunResult
{
    public ScenarioItem Item { get; set; } = null!;
    /// <summary>True only when the item passed; false when it failed or was inconclusive.</summary>
    public bool Passed => Failures.Count == 0 && Inconclusive == null;
    public List<string> Failures { get; set; } = new();
    /// <summary>Where the item's live replay left its recording and stopped, or null. When set, its assertions were not evaluated.</summary>
    public ReplayStop? Inconclusive { get; set; }
    public VevalExecutionContext? Context { get; set; }
}
