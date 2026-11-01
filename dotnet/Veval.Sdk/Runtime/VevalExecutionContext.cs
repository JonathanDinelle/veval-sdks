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

    public async Task<T> TrackStepAsync<T>(string name, object? input, Func<Task<T>> step)
        => await TrackStepAsync(name, input, _ => step());

    public async Task<T> TrackStepAsync<T>(string name, object? input, Func<StepHandle, Task<T>> step)
    {
        if (_recorded != null)
        {
            // Once a live replay has stopped, nothing after it can be served truthfully, even if the agent caught the stop.
            if (_fidelity!.Stop is { } stopped) throw new ReplayStoppedException(stopped);

            _stepRules.TryGetValue(name, out var rule);
            var recorded = FindRecording(name, input, rule, out var exact);
            var recordedType = recorded?.Data.Type ?? RecordedTypeOf(name);
            // In live mode a model step runs for real, including extra calls of a step recorded as a model step.
            var runLive = _liveModelSteps && recordedType == "llm";

            if (recorded == null && !runLive && _strictMockMode)
            {
                if (_liveModelSteps) throw StopAt(name, ReplayStopReason.NoRecording, null, input, recordedType);
                throw new InvalidOperationException(
                    $"Replay mode: no mock output for step '{name}'. " +
                    $"Available steps: {AvailableSteps()}. " +
                    "This would have made a real LLM call.");
            }

            // A live model handed a recording from another path would reason over a page it never reached.
            if (_liveModelSteps && !runLive && recorded != null && !exact && StopsOnDivergence(rule, recorded.Data.Type))
                throw StopAt(name, ReplayStopReason.Diverged, recorded.Data.Input, input, recordedType);

            if (recorded != null) recorded.Consumed = true;

            if (runLive)
            {
                _fidelity!.Record(new ReplayBinding(name, ReplayBindingKind.Live, recorded?.Index, recorded?.Data.Input, input));
            }
            else if (recorded != null)
            {
                var kind = exact ? ReplayBindingKind.Matched : ReplayBindingKind.Diverged;
                _fidelity!.Record(new ReplayBinding(name, kind, recorded.Index, recorded.Data.Input, input));

                var mock = recorded.Data.Output;
                T result;
                if (mock is T typed) result = typed;
                else if (mock != null)
                {
                    var json = JsonSerializer.Serialize(mock);
                    result = JsonSerializer.Deserialize<T>(json)!;
                }
                else result = default!;

                // Keep the recorded type so ToolCalled and snapshot types behave the same as in the original run.
                var mocked = new Step(name, null) { Input = input, Type = recorded.Data.Type };
                mocked.Metadata["_source"] = "replay";
                mocked.Complete(result);
                _steps.Add(mocked);
                return result;
            }
        }

        var s = new Step(name, null) { Input = input };
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
        _fidelity = new ReplayFidelity(_recorded.Select(r => new UnusedRecording(r.Data.Name, r.Index)));
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
                using var doc = JsonDocument.Parse(text);
                return SnapshotValues.Canonicalize(doc.RootElement.Clone(), ignore);
            }
            catch (JsonException) { /* not JSON after all: compare the text */ }
        }
        return SnapshotValues.Canonicalize(input, ignore);
    }

    /// <summary>Whether a diverged call of this step stops a live replay: the step's rule decides, else its recorded type.</summary>
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
        var message = $"Live replay stopped at step {position} ('{name}'): {detail} " +
                      "The replay is inconclusive: it neither passed nor failed." + advice;

        var stop = new ReplayStop(position, name, reason, recordedInput, input, message);
        _fidelity!.Stopped(stop);

        var step = new Step(name, null) { Input = input, Type = recordedType ?? "custom" };
        step.Metadata["_source"] = "replay";
        step.Fail(message);
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
