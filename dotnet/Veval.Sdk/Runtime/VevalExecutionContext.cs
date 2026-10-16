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
            var recorded = FindRecording(name, input, out var exact);
            // In live mode a model step runs for real, including extra calls of a step recorded as a model step.
            var runLive = _liveModelSteps && (recorded?.Data.Type ?? RecordedTypeOf(name)) == "llm";

            if (recorded == null && !runLive && _strictMockMode)
                throw new InvalidOperationException(_liveModelSteps
                    ? $"Live replay: step '{name}' has no recording to serve, and only steps recorded as 'llm' run live. " +
                      $"Available steps: {AvailableSteps()}. Running it could have reached a real system."
                    : $"Replay mode: no mock output for step '{name}'. " +
                      $"Available steps: {AvailableSteps()}. " +
                      "This would have made a real LLM call.");

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
    internal void LoadMockOutputs(TraceData trace, bool strict = true, bool liveModelSteps = false)
    {
        _strictMockMode = strict;
        _liveModelSteps = liveModelSteps;

        if (trace.Steps.Count == 0)
            throw new InvalidOperationException(
                $"Replay trace '{trace.TraceId}' has no steps. Cannot mock LLM calls. " +
                "Ensure the trace was recorded with steps before using it for replay.");

        _recorded = trace.Steps.Select((step, i) => new RecordedStep(step, i)).ToList();
        _fidelity = new ReplayFidelity(_recorded.Select(r => new UnusedRecording(r.Data.Name, r.Index)));
    }

    /// <summary>
    /// Picks the recorded step for a call: among unused recordings with the same name, the one whose input is
    /// identical (key order ignored), otherwise the earliest — which is reported as diverged.
    /// </summary>
    private RecordedStep? FindRecording(string name, object? input, out bool exact)
    {
        exact = false;
        var candidates = _recorded!.Where(r => !r.Consumed && r.Data.Name == name).ToList();
        if (candidates.Count == 0) return null;

        var actual = SnapshotValues.Canonicalize(input, Array.Empty<string>());
        var match = candidates.FirstOrDefault(r => r.CanonicalInput == actual);
        exact = match != null;
        return match ?? candidates[0];
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

        public RecordedStep(StepData data, int index)
        {
            Data = data;
            Index = index;
            CanonicalInput = SnapshotValues.Canonicalize(data.Input, Array.Empty<string>());
        }
    }
}
