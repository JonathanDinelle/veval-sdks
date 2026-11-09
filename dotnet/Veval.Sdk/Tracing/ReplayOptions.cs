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

    /// <summary>
    /// Per step name: how calls match their recordings (name only, or with some input fields ignored) and, in a
    /// live replay, whether a diverged call stops the run or is served. Matching rules apply to mocked replays too.
    /// </summary>
    public Dictionary<string, ReplayStepRule> StepRules { get; set; } = new();

    /// <summary>
    /// Live replay: steps recorded with one of these types stop the run when a call diverges, unless their
    /// <see cref="StepRules"/> entry says otherwise. A stopped replay is inconclusive (<see cref="ReplayResult.Inconclusive"/>):
    /// past that step the recording can't say what the real tool would have returned. Defaults to <c>tool</c>; other
    /// diverged steps are served the next unused recording and reported in <see cref="ReplayResult.Fidelity"/>.
    /// </summary>
    public ISet<string> StopOnDivergenceTypes { get; set; } = new HashSet<string> { "tool" };

    /// <summary>
    /// Failures to serve instead of recorded outputs: a timeout, an HTTP error, a rate limit, a malformed output or a
    /// custom exception (<see cref="ReplayFault"/>), so the replay runs the agent's retries, fallbacks and guards.
    /// Needs a mocked or live replay. Once a fault has fired, a call that matches no unused recording stops the
    /// replay inconclusive instead of being served a recording from another path; in a live replay, model steps
    /// still run live, so the model's reaction to the error is tested. A fault that never fires fails the replay.
    /// </summary>
    public List<ReplayFault> Faults { get; set; } = new();
}
