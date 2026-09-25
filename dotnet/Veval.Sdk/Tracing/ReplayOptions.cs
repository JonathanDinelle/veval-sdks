using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public class ReplayOptions
{
    public bool MockLlmResponses { get; set; }
    public ITraceAssertion[] Assertions { get; set; } = Array.Empty<ITraceAssertion>();

    /// <summary>
    /// When set, the replay also fails if its step sequence or step inputs drifted from the recorded trace.
    /// Recorded outputs are served by step name, so a control-flow change can bind an output to the wrong
    /// call and still pass every assertion — this check catches that.
    /// </summary>
    public SnapshotOptions? CompareWithRecording { get; set; }
}
