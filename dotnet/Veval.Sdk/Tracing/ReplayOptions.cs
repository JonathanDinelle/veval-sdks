using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public class ReplayOptions
{
    public bool MockLlmResponses { get; set; }
    public ITraceAssertion[] Assertions { get; set; } = Array.Empty<ITraceAssertion>();

    /// <summary>
    /// When set, the replay also fails if its step sequence or step inputs drifted from the recorded trace.
    /// Without it, a call whose input differs from every recording of that step is still served (the next
    /// unused recording in order) and only reported in <see cref="ReplayResult.Fidelity"/>.
    /// </summary>
    public SnapshotOptions? CompareWithRecording { get; set; }
}
