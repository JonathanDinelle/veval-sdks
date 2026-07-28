using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public class ScenarioItem
{
    public string? Name { get; set; }
    public string? TraceId { get; set; }
    public object? Input { get; set; }
    public ITraceAssertion[] Assertions { get; set; } = Array.Empty<ITraceAssertion>();
}
