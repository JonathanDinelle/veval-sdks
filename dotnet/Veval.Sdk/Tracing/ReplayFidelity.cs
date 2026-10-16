namespace Veval.Sdk;

/// <summary>How a replayed call was bound to the recording.</summary>
public enum ReplayBindingKind
{
    /// <summary>Served the recorded step with the same name and an identical input.</summary>
    Matched,
    /// <summary>No recorded step with that name had an identical input; served the next unused one in recorded order.</summary>
    Diverged,
    /// <summary>A model step that ran for real in a live replay (<see cref="ReplayOptions.LiveModelSteps"/>).</summary>
    Live,
}

/// <summary>
/// One replayed call and the recorded step bound to it. <see cref="RecordedIndex"/> is null for a live model call
/// beyond the ones recorded.
/// </summary>
public record ReplayBinding(string StepName, ReplayBindingKind Kind, int? RecordedIndex, object? RecordedInput, object? ActualInput);

/// <summary>A recorded step the replay never called.</summary>
public record UnusedRecording(string StepName, int RecordedIndex);

/// <summary>
/// How faithfully a mocked replay followed its recording. A replay is faithful when every call matched a
/// recorded step with an identical input and every recorded step was called.
/// </summary>
public class ReplayFidelity
{
    private readonly List<ReplayBinding> _bindings = new();
    private readonly List<UnusedRecording> _unused;

    internal ReplayFidelity(IEnumerable<UnusedRecording> recorded) => _unused = recorded.ToList();

    /// <summary>Each replayed call, in call order.</summary>
    public IReadOnlyList<ReplayBinding> Bindings => _bindings;
    /// <summary>Recorded steps no call consumed, in recorded order.</summary>
    public IReadOnlyList<UnusedRecording> Unused => _unused;
    public int Matched => _bindings.Count(b => b.Kind == ReplayBindingKind.Matched);
    public int Diverged => _bindings.Count(b => b.Kind == ReplayBindingKind.Diverged);
    /// <summary>Model steps that ran for real in a live replay.</summary>
    public int Live => _bindings.Count(b => b.Kind == ReplayBindingKind.Live);
    /// <summary>No call diverged and every recorded step was called. Live calls don't count against it.</summary>
    public bool IsFaithful => Diverged == 0 && _unused.Count == 0;

    internal void Record(ReplayBinding binding)
    {
        _bindings.Add(binding);
        if (binding.RecordedIndex is { } index) _unused.RemoveAll(u => u.RecordedIndex == index);
    }

    /// <summary>
    /// The replay's binding failure, or null. A mocked replay fails when a recorded step was never called; a live
    /// replay fails when no step ran live (a live model may legitimately skip recorded steps). Not called when the
    /// agent threw, since the exception already explains the rest.
    /// </summary>
    internal string? Failure(string traceId, bool liveModelSteps) =>
        liveModelSteps ? NoLiveStepFailure(traceId) : UnusedFailure(traceId);

    /// <summary>The failure for recorded steps that were never called, or null when every recording was used.</summary>
    internal string? UnusedFailure(string traceId)
    {
        if (_unused.Count == 0) return null;
        var steps = string.Join(", ", _unused.Select(u => $"'{u.StepName}' (recorded step {u.RecordedIndex + 1})"));
        return $"Replay never called {_unused.Count} recorded step(s) from trace {traceId}: {steps}. " +
               "The agent skipped part of the recorded run, so its assertions may be passing on a path the recording does not cover.";
    }

    /// <summary>The failure for a live replay in which nothing ran live, or null when at least one model step did.</summary>
    internal string? NoLiveStepFailure(string traceId) => Live > 0 ? null :
        $"Live replay of trace {traceId} ran no step live: none of the steps it called was recorded as type 'llm', so every " +
        "output came from the recording and a prompt change could not show up. Mark model steps with " +
        "SetMeta(\"type\", \"llm\") and record a new trace, or replay in mocked mode.";
}
