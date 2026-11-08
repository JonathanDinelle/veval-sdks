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
    /// <summary>Served an injected fault (<see cref="ReplayOptions.Faults"/>) instead of running or using a recording.</summary>
    Faulted,
}

/// <summary>
/// One replayed call and the recorded step bound to it. <see cref="RecordedIndex"/> is null for a live model call
/// beyond the ones recorded.
/// </summary>
public record ReplayBinding(string StepName, ReplayBindingKind Kind, int? RecordedIndex, object? RecordedInput, object? ActualInput)
{
    /// <summary>
    /// The type the call site gave (<see cref="StepOptions.Type"/>) when it differs from the recorded type, or null.
    /// The recorded type won: a mislabelled call can't turn a recorded tool step live.
    /// </summary>
    public string? DeclaredType { get; init; }
}

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
    private readonly List<ReplayFault> _faults;
    private readonly int[] _fired;

    internal ReplayFidelity(IEnumerable<UnusedRecording> recorded, IReadOnlyList<ReplayFault>? faults = null)
    {
        _unused = recorded.ToList();
        _faults = faults?.ToList() ?? new List<ReplayFault>();
        _fired = new int[_faults.Count];
    }

    /// <summary>Each replayed call, in call order.</summary>
    public IReadOnlyList<ReplayBinding> Bindings => _bindings;
    /// <summary>Recorded steps no call consumed, in recorded order.</summary>
    public IReadOnlyList<UnusedRecording> Unused => _unused;
    public int Matched => _bindings.Count(b => b.Kind == ReplayBindingKind.Matched);
    public int Diverged => _bindings.Count(b => b.Kind == ReplayBindingKind.Diverged);
    /// <summary>Model steps that ran for real in a live replay.</summary>
    public int Live => _bindings.Count(b => b.Kind == ReplayBindingKind.Live);
    /// <summary>Calls served an injected fault.</summary>
    public int Faulted => _bindings.Count(b => b.Kind == ReplayBindingKind.Faulted);
    /// <summary>Each fault in <see cref="ReplayOptions.Faults"/> and how many times it fired, in declared order.</summary>
    public IReadOnlyList<FaultReport> Faults => _faults.Select((f, i) => new FaultReport(f, _fired[i])).ToList();
    /// <summary>The first fault that fired, or null while none has.</summary>
    internal ReplayFault? FirstFired { get; private set; }
    /// <summary>No call diverged and every recorded step was called. Live calls don't count against it.</summary>
    public bool IsFaithful => Diverged == 0 && _unused.Count == 0;
    /// <summary>Where a live replay left its recording and stopped, or null when it didn't.</summary>
    public ReplayStop? Stop { get; private set; }

    internal void Stopped(ReplayStop stop) => Stop ??= stop;

    internal IReadOnlyList<ReplayFault> DeclaredFaults => _faults;
    internal int FiredCount(int faultIndex) => _fired[faultIndex];

    internal void Fired(int faultIndex)
    {
        _fired[faultIndex]++;
        FirstFired ??= _faults[faultIndex];
    }

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
        liveModelSteps ? NoLiveStepFailure(traceId) : UnusedRecordingsExpected ? null : UnusedFailure(traceId);

    /// <summary>
    /// A fault that fails every call fired, so the agent took its fallback path and the rest of the recording may
    /// legitimately go unused. A fault limited with <see cref="ReplayFault.Times"/> is expected to rejoin the recording.
    /// </summary>
    private bool UnusedRecordingsExpected => _faults.Where((f, i) => f.Count == null && _fired[i] > 0).Any();

    /// <summary>One failure per declared fault that never fired: a fault test whose fault didn't fire tested nothing.</summary>
    internal IEnumerable<string> UnfiredFaultFailures(Func<string, int> callsOf) =>
        _faults.Where((f, i) => _fired[i] == 0).Select(f =>
        {
            var calls = callsOf(f.StepName);
            var why = calls == 0
                ? $"the agent never called '{f.StepName}'"
                : $"none of the {calls} call(s) of '{f.StepName}' matched it";
            return $"Fault not triggered: {f.Description} never fired: {why}. The failure path it was meant to test did not run.";
        });

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
