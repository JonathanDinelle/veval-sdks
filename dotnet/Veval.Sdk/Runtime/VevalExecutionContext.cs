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

    /// <summary>Steps recorded so far, in order — readable by custom assertions.</summary>
    public IReadOnlyList<Step> Steps => _steps;
    /// <summary>Trace-level metadata set with <see cref="SetMetadata"/>.</summary>
    public IReadOnlyDictionary<string, object> TraceMeta => _metadata;
    /// <summary>Judge verdicts recorded by <c>TraceAssert.Judge</c> during this run.</summary>
    public IReadOnlyList<JudgeRecord> Judgments => _judgments;
    /// <summary>How each replayed step was bound to the recording; null when the run is not a mocked replay.</summary>
    public ReplayFidelity? ReplayFidelity => _fidelity;

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
            var recorded = BindRecordedStep(name, input);
            if (recorded == null && _strictMockMode)
                throw new InvalidOperationException(
                    $"Replay mode: no mock output for step '{name}'. " +
                    $"Available steps: {string.Join(", ", _recorded.Where(r => !r.Consumed).Select(r => r.Data.Name).Distinct())}. " +
                    "This would have made a real LLM call.");

            if (recorded != null)
            {
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

    internal void LoadMockOutputs(TraceData trace, bool strict = true)
    {
        _strictMockMode = strict;

        if (trace.Steps.Count == 0)
            throw new InvalidOperationException(
                $"Replay trace '{trace.TraceId}' has no steps. Cannot mock LLM calls. " +
                "Ensure the trace was recorded with steps before using it for replay.");

        _recorded = trace.Steps.Select((step, i) => new RecordedStep(step, i)).ToList();
        _fidelity = new ReplayFidelity(_recorded.Select(r => new UnusedRecording(r.Data.Name, r.Index)));
    }

    /// <summary>
    /// Picks the recorded step to serve for a call: among unused recordings with the same name, the one whose
    /// input is identical (key order ignored), otherwise the earliest — which is reported as diverged.
    /// </summary>
    private RecordedStep? BindRecordedStep(string name, object? input)
    {
        var candidates = _recorded!.Where(r => !r.Consumed && r.Data.Name == name).ToList();
        if (candidates.Count == 0) return null;

        var actual = SnapshotValues.Canonicalize(input, Array.Empty<string>());
        var recorded = candidates.FirstOrDefault(r => r.CanonicalInput == actual) ?? candidates[0];
        recorded.Consumed = true;

        var kind = recorded.CanonicalInput == actual ? ReplayBindingKind.Matched : ReplayBindingKind.Diverged;
        _fidelity!.Record(new ReplayBinding(name, kind, recorded.Index, recorded.Data.Input, input));
        return recorded;
    }

    private sealed class RecordedStep
    {
        public StepData Data { get; }
        public int Index { get; }
        public string CanonicalInput { get; }
        public bool Consumed { get; set; }

        public RecordedStep(StepData data, int index)
        {
            Data = data;
            Index = index;
            CanonicalInput = SnapshotValues.Canonicalize(data.Input, Array.Empty<string>());
        }
    }
}
