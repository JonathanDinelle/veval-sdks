using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public class ReplayOptions
{
    /// <summary>
    /// Serve every step's recorded output instead of running it. Deterministic and free, so it checks control
    /// flow and code changes; it can't detect a prompt regression, because model calls return the old outputs.
    /// </summary>
    public bool MockLlmResponses { get; set; }

    /// <summary>
    /// Run steps recorded as <c>llm</c> for real and serve every other step (tools, untyped steps) from the
    /// recording, so a prompt change shows up while tools never reach a real system. Takes precedence over
    /// <see cref="MockLlmResponses"/>. The replay fails if no step ran live.
    /// </summary>
    public bool LiveModelSteps { get; set; }
    public ITraceAssertion[] Assertions { get; set; } = Array.Empty<ITraceAssertion>();

    /// <summary>
    /// When set, the replay also fails if its step sequence or step inputs drifted from the recorded trace.
    /// Without it, a call whose input differs from every recording of that step is still served (the next
    /// unused recording in order) and only reported in <see cref="ReplayResult.Fidelity"/>.
    /// </summary>
    public SnapshotOptions? CompareWithRecording { get; set; }
}
