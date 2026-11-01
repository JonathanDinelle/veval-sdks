namespace Veval.Sdk;

public class ScenarioRunResult
{
    /// <summary>True when every item passed: a failed or inconclusive item makes the run not pass.</summary>
    public bool Passed => Results.All(r => r.Passed);
    public int PassCount => Results.Count(r => r.Passed);
    /// <summary>Items that failed. Inconclusive items are counted in <see cref="InconclusiveCount"/>, not here.</summary>
    public int FailCount => Results.Count(r => !r.Passed && r.Inconclusive == null);
    /// <summary>Items whose live replay left its recording and stopped, so they neither passed nor failed.</summary>
    public int InconclusiveCount => Results.Count(r => r.Inconclusive != null);
    public List<ItemRunResult> Results { get; set; } = new();
}
