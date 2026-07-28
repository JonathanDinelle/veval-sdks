namespace Veval.Sdk;

public class ItemRunResult
{
    public ScenarioItem Item { get; set; } = null!;
    public bool Passed => Failures.Count == 0;
    public List<string> Failures { get; set; } = new();
    public VevalExecutionContext? Context { get; set; }
}
