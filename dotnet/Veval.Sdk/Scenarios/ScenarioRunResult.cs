namespace Veval.Sdk;

public class ScenarioRunResult
{
    public bool Passed => Results.All(r => r.Passed);
    public int PassCount => Results.Count(r => r.Passed);
    public int FailCount => Results.Count(r => !r.Passed);
    public List<ItemRunResult> Results { get; set; } = new();
}
