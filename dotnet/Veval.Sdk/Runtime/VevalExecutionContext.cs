using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Veval.Sdk.Internal;

namespace Veval.Sdk;

/// <summary>A judge verdict. Serialized in lowercase to match the Node and Python SDKs and the dashboard.</summary>
public record JudgeRecord(
    [property: JsonPropertyName("criteria")] string Criteria,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("reasoning")] string Reasoning);

public class VevalExecutionContext
{
    public string TraceId { get; }
    public object? Input { get; }

    private readonly List<Step> _steps = new();
    private readonly Dictionary<string, object> _metadata = new();
    private readonly List<JudgeRecord> _judgments = new();
    private List<RecordedStep>? _recorded;
    private ReplayFidelity? _fidelity;
    private bool _strictMockMode;
    private bool _liveModelSteps;
    private IReadOnlyDictionary<string, ReplayStepRule> _stepRules = new Dictionary<string, ReplayStepRule>();
    private ISet<string> _stopOnDivergenceTypes = new HashSet<string> { "tool" };
    private readonly Dictionary<string, int> _callCounts = new();

    /// <summary>Steps recorded so far, in order — readable by custom assertions.</summary>
    public IReadOnlyList<Step> Steps => _steps;
    /// <summary>Trace-level metadata set with <see cref="SetMetadata"/>.</summary>
    public IReadOnlyDictionary<string, object> TraceMeta => _metadata;
    /// <summary>Judge verdicts recorded by <c>TraceAssert.Judge</c> during this run.</summary>
    public IReadOnlyList<JudgeRecord> Judgments => _judgments;
    /// <summary>How each replayed step was bound to the recording; null when the run doesn't replay a recording.</summary>
    public ReplayFidelity? ReplayFidelity => _fidelity;
    /// <summary>
    /// True when this run replays a recorded trace: <c>ReplayAsync</c>, a trace-backed scenario item, or
    /// <c>VevalTestSdk.WithReplay</c>, mocked or live. Check it before a side effect that isn't inside a step
    /// served from the recording, such as sending an email; in a live replay, model steps run for real.
    /// </summary>
    public bool IsReplay { get; internal set; }

    public VevalExecutionContext(string traceId, object? input)
    {
        TraceId = traceId;
        Input = input;
    }

    public Task<T> TrackStepAsync<T>(string name, object? input, Func<Task<T>> step)
        => TrackStepAsync(name, input, _ => step(), NoOptions);

    public Task<T> TrackStepAsync<T>(string name, object? input, Func<StepHandle, Task<T>> step)
        => TrackStepAsync(name, input, step, NoOptions);

    /// <param name="options">Settings known before the step runs, such as its type (<see cref="StepOptions.Type"/>).</param>
    public Task<T> TrackStepAsync<T>(string name, object? input, Func<Task<T>> step, StepOptions options)
        => TrackStepAsync(name, input, _ => step(), options);

    /// <param name="options">Settings known before the step runs, such as its type (<see cref="StepOptions.Type"/>).</param>
    public async Task<T> TrackStepAsync<T>(string name, object? input, Func<StepHandle, Task<T>> step, StepOptions options)
    {
        var declaredType = options.Type;
        string? effectiveType = declaredType;
        if (_recorded != null)
        {
            // Once a live replay has stopped, nothing after it can be served truthfully, even if the agent caught the stop.
            if (_fidelity!.Stop is { } stopped) throw new ReplayStoppedException(stopped);

            _stepRules.TryGetValue(name, out var rule);
            _callCounts[name] = _callCounts.TryGetValue(name, out var calls) ? calls + 1 : 1;
            if (FaultFor(name, input, _callCounts[name], rule) is { } faultIndex)
                return ServeFault<T>(name, input, faultIndex, rule, declaredType);

            var recorded = FindRecording(name, input, rule, out var exact);
            var recordedType = recorded?.Data.Type ?? RecordedTypeOf(name);
            // A recorded llm or tool type wins; a recorded custom type is what an untyped step is saved as, so it says
            // nothing and the call site's type applies. A mislabelled call still can't turn a recorded tool step live.
            var knownType = recordedType is null or StepType.Custom ? null : recordedType;
            effectiveType = knownType ?? declaredType ?? recordedType;
            // In live mode a model step runs for real, including extra calls of a step recorded as a model step and
            // model steps the recording never contained.
            var runLive = _liveModelSteps && effectiveType == StepType.Llm;
            var conflictingType = knownType != null && declaredType != null && declaredType != knownType
                ? declaredType : null;

            // After a fault, a mocked replay serves only calls that still match their recording: a fallback the
            // recording never called, or a model call whose prompt now carries the error, has no truthful output.
            if (!_liveModelSteps && _fidelity!.FirstFired != null && (recorded == null || !exact))
                throw StopAt(name, recorded == null ? ReplayStopReason.NoRecording : ReplayStopReason.Diverged,
                    recorded?.Data.Input, input, effectiveType);

            if (recorded == null && !runLive && _strictMockMode)
            {
                if (_liveModelSteps) throw StopAt(name, ReplayStopReason.NoRecording, null, input, effectiveType);
                throw new InvalidOperationException(
                    $"Replay mode: no mock output for step '{name}'. " +
                    $"Available steps: {AvailableSteps()}. " +
                    "This would have made a real LLM call.");
            }

            // A live model handed a recording from another path would reason over a page it never reached.
            if (_liveModelSteps && !runLive && recorded != null && !exact && StopsOnDivergence(rule, effectiveType))
                throw StopAt(name, ReplayStopReason.Diverged, recorded.Data.Input, input, effectiveType);

            if (recorded != null) recorded.Consumed = true;

            if (runLive)
            {
                _fidelity!.Record(new ReplayBinding(name, ReplayBindingKind.Live, recorded?.Index, recorded?.Data.Input, input)
                    { DeclaredType = conflictingType });
            }
            else if (recorded != null)
            {
                var kind = exact ? ReplayBindingKind.Matched : ReplayBindingKind.Diverged;
                _fidelity!.Record(new ReplayBinding(name, kind, recorded.Index, recorded.Data.Input, input)
                    { DeclaredType = conflictingType });

                var result = ConvertOutput<T>(recorded.Data.Output);

                // The recorded type, or the call site's when the recording saved the step untyped, so ToolCalled and
                // snapshot types match the original run wherever the recording knew the step's type.
                var mocked = new Step(name, null) { Input = input, Type = effectiveType ?? StepType.Custom };
                mocked.Metadata["_source"] = "replay";
                mocked.Complete(result);
                _steps.Add(mocked);
                return result;
            }
        }

        var s = new Step(name, null) { Input = input, Type = effectiveType ?? StepType.Custom };
        _steps.Add(s);
        var handle = new StepHandle(s);
        try
        {
            var result = await step(handle);
            s.Complete(result);
            return result;
        }
        catch (Exception ex)
        {
            s.Fail(ex.Message);
            throw;
        }
    }

    private static readonly StepOptions NoOptions = new();

    /// <summary>A recorded (or injected) output as the step's return type, converting through JSON when needed.</summary>
    private static T ConvertOutput<T>(object? value)
    {
        if (value is T typed) return typed;
        if (value == null) return default!;
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    /// <summary>The index of the first declared fault that applies to this call of the step, or null.</summary>
    private int? FaultFor(string name, object? input, int call, ReplayStepRule? rule)
    {
        var faults = _fidelity!.DeclaredFaults;
        for (var i = 0; i < faults.Count; i++)
        {
            var fault = faults[i];
            if (fault.StepName != name || call < fault.FromCall) continue;
            if (fault.Count is { } limit && _fidelity.FiredCount(i) >= limit) continue;
            if (fault.HasInput)
            {
                var ignore = rule?.IgnoreInputFields is { Count: > 0 } fields
                    ? fields.OrderBy(f => f, StringComparer.Ordinal).ToArray()
                    : Array.Empty<string>();
                if (IgnoringKey(fault.Input, ignore) != IgnoringKey(input, ignore)) continue;
            }
            return i;
        }
        return null;
    }

    /// <summary>
    /// Serves a fault at this call: records it as a step with <c>_source = "fault"</c> and either throws the fault's
    /// exception or returns its malformed output. The call uses up no recording, so a retry is served the recorded one.
    /// </summary>
    private T ServeFault<T>(string name, object? input, int faultIndex, ReplayStepRule? rule, string? declaredType)
    {
        var fault = _fidelity!.DeclaredFaults[faultIndex];
        _fidelity.Fired(faultIndex);
        _fidelity.Record(new ReplayBinding(name, ReplayBindingKind.Faulted, null, null, input));

        var recordedType = RecordedTypeOf(name);
        var type = recordedType is null or StepType.Custom ? declaredType ?? recordedType : recordedType;
        var step = new Step(name, null) { Input = input, Type = type ?? StepType.Custom };
        step.Metadata["_source"] = "fault";
        step.Metadata["_fault"] = fault.Description;
        _steps.Add(step);

        if (fault.Kind != FaultKind.Malformed)
        {
            var exception = fault.CreateException();
            step.Fail(exception.Message);
            throw exception;
        }

        T value;
        try
        {
            value = MalformedOutputOf<T>(fault, name, input, rule);
        }
        catch (Exception ex)
        {
            step.Fail(ex.Message);
            throw;
        }
        step.Complete(value);
        return value;
    }

    private T MalformedOutputOf<T>(ReplayFault fault, string name, object? input, ReplayStepRule? rule)
    {
        var holdsText = typeof(T) == typeof(string) || typeof(T) == typeof(object);
        switch (fault.MalformedOutput)
        {
            case MalformedOutput.Empty:
                return typeof(T) == typeof(string) ? (T)(object)"" : default!;
            case MalformedOutput.Truncated:
                if (!holdsText)
                    throw new InvalidOperationException(
                        $"Fault on '{name}': a truncated output is JSON text, and the step returns {typeof(T).Name}. " +
                        "Use it on a step that returns a string or an object, or give the malformed value yourself.");
                var recorded = FindRecording(name, input, rule, out _)
                    ?? throw new InvalidOperationException(
                        $"Fault on '{name}': there is no unused recording of the step to truncate. " +
                        "Give the malformed value yourself instead.");
                var text = recorded.Data.Output switch
                {
                    string s => s,
                    JsonElement { ValueKind: JsonValueKind.String } el => el.GetString() ?? "",
                    var other => JsonSerializer.Serialize(other),
                };
                return (T)(object)text.Substring(0, text.Length / 2);
            default:
                try
                {
                    return ConvertOutput<T>(fault.MalformedValue);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    throw new InvalidOperationException(
                        $"Fault on '{name}': the malformed value doesn't convert to {typeof(T).Name}, the step's return " +
                        "type. Use a value of that type, or make the step return an object.", ex);
                }
        }
    }

    public void SetMetadata(string key, object value) => _metadata[key] = value;

    internal void RecordJudgment(string criteria, double score, bool passed, string reasoning) =>
        _judgments.Add(new JudgeRecord(criteria, score, passed, reasoning));

    /// <param name="liveModelSteps">Run steps recorded as <c>llm</c> for real and serve every other step from the recording.</param>
    /// <param name="options">Step matching rules, and which diverged steps stop a live replay.</param>
    internal void LoadMockOutputs(TraceData trace, bool strict = true, bool liveModelSteps = false, ReplayOptions? options = null)
    {
        IsReplay = true;
        _strictMockMode = strict;
        _liveModelSteps = liveModelSteps;
        if (options != null)
        {
            _stepRules = options.StepRules;
            _stopOnDivergenceTypes = options.StopOnDivergenceTypes;
        }

        if (trace.Steps.Count == 0)
            throw new InvalidOperationException(
                $"Replay trace '{trace.TraceId}' has no steps. Cannot mock LLM calls. " +
                "Ensure the trace was recorded with steps before using it for replay.");

        _recorded = trace.Steps.Select((step, i) => new RecordedStep(step, i)).ToList();
        _fidelity = new ReplayFidelity(_recorded.Select(r => new UnusedRecording(r.Data.Name, r.Index)), options?.Faults);
    }

    /// <summary>
    /// Picks the recorded step for a call: among unused recordings with the same name, the one whose input is
    /// identical (key order and the rule's ignored fields aside), otherwise the earliest — which is reported as
    /// diverged. A name-only rule matches the earliest.
    /// </summary>
    private RecordedStep? FindRecording(string name, object? input, ReplayStepRule? rule, out bool exact)
    {
        exact = false;
        var candidates = _recorded!.Where(r => !r.Consumed && r.Data.Name == name).ToList();
        if (candidates.Count == 0) return null;
        if (rule?.MatchOnNameOnly == true)
        {
            exact = true;
            return candidates[0];
        }

        if (rule?.IgnoreInputFields is not { Count: > 0 } fields)
        {
            var key = SnapshotValues.Canonicalize(input, Array.Empty<string>());
            var identical = candidates.FirstOrDefault(r => r.CanonicalInput == key);
            exact = identical != null;
            return identical ?? candidates[0];
        }

        var ignore = fields.OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var actual = IgnoringKey(input, ignore);
        var match = candidates.FirstOrDefault(r => r.KeyIgnoring(ignore) == actual);
        exact = match != null;
        return match ?? candidates[0];
    }

    /// <summary>
    /// The matching key of an input with some fields dropped. An input that is a JSON object or array encoded as a
    /// string, such as a model's tool-call arguments, is parsed first, so its fields can be dropped too.
    /// </summary>
    private static string IgnoringKey(object? input, string[] ignore)
    {
        var text = input switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null,
        };
        var trimmed = text?.TrimStart();
        if (trimmed is { Length: > 0 } && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return SnapshotValues.Canonicalize(doc.RootElement.Clone(), ignore);
            }
            catch (JsonException) { /* not JSON after all: compare the text */ }
        }
        return SnapshotValues.Canonicalize(input, ignore);
    }

    /// <summary>Whether a diverged call of this step stops a live replay: the step's rule decides, else its type.</summary>
    private bool StopsOnDivergence(ReplayStepRule? rule, string? recordedType) =>
        rule?.OnDivergence is { } policy
            ? policy == OnDivergence.Stop
            : recordedType != null && _stopOnDivergenceTypes.Contains(recordedType);

    /// <summary>
    /// Stops a live replay at this call: records the stop and a failed step for it, and returns the exception that
    /// ends the run.
    /// </summary>
    private ReplayStoppedException StopAt(string name, ReplayStopReason reason, object? recordedInput, object? input, string? recordedType)
    {
        var position = _steps.Count + 1;
        var detail = reason == ReplayStopReason.Diverged
            ? $"its input matches no unused recording of '{name}', so what the real step would return is unknown. " +
              $"Recorded input: {OneLine(recordedInput)}. Actual input: {OneLine(input)}."
            : $"the recording has no unused call of '{name}' to serve ({RecordedCallsOf(name)}).";
        var advice = reason == ReplayStopReason.Diverged
            ? " If the model took a different path, record that path. If your code changed how the step is called, " +
              "re-record, or replay mocked to test this change. If this input doesn't affect the step's output, " +
              "match it on name only (StepRules)."
            : " If the model took a different path, record that path. If your code added this call, re-record, or " +
              "replay mocked to test this change.";
        var fault = _fidelity!.FirstFired;
        if (fault != null && !_liveModelSteps)
            advice = " Past the fault the agent took a path the recording doesn't cover (a fallback it never called, or a " +
                     "call whose input now carries the error), so no recorded output can stand in for it. Replay live to run " +
                     "model steps for real, or record a trace of this path.";
        var where = fault != null ? $" after an injected fault ({fault.Description})" : "";
        var message = $"{(_liveModelSteps ? "Live replay" : "Replay")} stopped at step {position} ('{name}'){where}: {detail} " +
                      "The replay is inconclusive: it neither passed nor failed." + advice;

        var stop = new ReplayStop(position, name, reason, recordedInput, input, message);
        _fidelity!.Stopped(stop);

        var step = new Step(name, null) { Input = input, Type = recordedType ?? "custom" };
        step.Metadata["_source"] = "replay";
        step.Stopped(message);
        _steps.Add(step);
        return new ReplayStoppedException(stop);
    }

    private static readonly JsonSerializerOptions OneLineJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>An input as compact JSON for a message, as the Node and Python SDKs print it.</summary>
    private static string OneLine(object? value)
    {
        try { return JsonSerializer.Serialize(value, OneLineJson); }
        catch (Exception) { return value?.ToString() ?? "null"; }
    }

    private string RecordedCallsOf(string name)
    {
        var count = _recorded!.Count(r => r.Data.Name == name);
        return count == 0 ? "the trace never recorded it" : $"its {count} recorded call(s) were already used";
    }

    /// <summary>The recorded type of a step name, used and unused alike; null when the trace never recorded it.</summary>
    private string? RecordedTypeOf(string name) => _recorded!.FirstOrDefault(r => r.Data.Name == name)?.Data.Type;

    private string AvailableSteps() =>
        string.Join(", ", _recorded!.Where(r => !r.Consumed).Select(r => r.Data.Name).Distinct());

    private sealed class RecordedStep
    {
        public StepData Data { get; }
        public int Index { get; }
        public string CanonicalInput { get; }
        public bool Consumed { get; set; }
        private readonly Dictionary<string, string> _keysIgnoring = new();

        /// <summary>The input's matching key with these (sorted) fields dropped, computed once per field set.</summary>
        public string KeyIgnoring(string[] ignore)
        {
            var cacheKey = string.Join("\u0001", ignore);
            if (!_keysIgnoring.TryGetValue(cacheKey, out var key))
                _keysIgnoring[cacheKey] = key = IgnoringKey(Data.Input, ignore);
            return key;
        }

        public RecordedStep(StepData data, int index)
        {
            Data = data;
            Index = index;
            CanonicalInput = SnapshotValues.Canonicalize(data.Input, Array.Empty<string>());
        }
    }
}
