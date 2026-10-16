using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public class ScenarioItem
{
    public string? Name { get; set; }
    public string? TraceId { get; set; }
    public object? Input { get; set; }
    public ITraceAssertion[] Assertions { get; set; } = Array.Empty<ITraceAssertion>();

    /// <summary>
    /// For a trace-backed item: serve model steps from the recording too (mocked replay) instead of running
    /// them live. Cheaper and deterministic, so it suits code changes, but it can't catch a prompt regression.
    /// </summary>
    public bool MockModelSteps { get; set; }
}
