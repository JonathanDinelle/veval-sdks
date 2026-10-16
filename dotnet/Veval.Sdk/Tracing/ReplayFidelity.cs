namespace Veval.Sdk;

/// <summary>How a replayed call was bound to the recording.</summary>
public enum ReplayBindingKind
{
    /// <summary>Served the recorded step with the same name and an identical input.</summary>
    Matched,
    /// <summary>No recorded step with that name had an identical input; served the next unused one in recorded order.</summary>
    Diverged,
}

/// <summary>One replayed call and the recorded step that served it.</summary>
public record ReplayBinding(string StepName, ReplayBindingKind Kind, int RecordedIndex, object? RecordedInput, object? ActualInput);

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
    public bool IsFaithful => Diverged == 0 && _unused.Count == 0;

    internal void Record(ReplayBinding binding)
    {
        _bindings.Add(binding);
        _unused.RemoveAll(u => u.RecordedIndex == binding.RecordedIndex);
    }

    /// <summary>The failure for recorded steps that were never called, or null when every recording was used.</summary>
    internal string? UnusedFailure(string traceId)
    {
        if (_unused.Count == 0) return null;
        var steps = string.Join(", ", _unused.Select(u => $"'{u.StepName}' (recorded step {u.RecordedIndex + 1})"));
        return $"Replay never called {_unused.Count} recorded step(s) from trace {traceId}: {steps}. " +
               "The agent skipped part of the recorded run, so its assertions may be passing on a path the recording does not cover.";
    }
}
