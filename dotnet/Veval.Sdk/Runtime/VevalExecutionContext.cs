using System.Text.Json;

namespace Veval.Sdk;

public record JudgeRecord(string Criteria, double Score, bool Passed, string Reasoning);

public class VevalExecutionContext
{
    public string TraceId { get; }
    public object? Input { get; }

    private readonly List<Step> _steps = new();
    private readonly Dictionary<string, object> _metadata = new();
    private readonly List<JudgeRecord> _judgments = new();
    private Dictionary<string, Queue<StepData>>? _mockOutputs;
    private bool _strictMockMode;

    internal IReadOnlyList<Step> Steps => _steps;
    internal IReadOnlyDictionary<string, object> TraceMeta => _metadata;
    internal IReadOnlyList<JudgeRecord> Judgments => _judgments;

    public VevalExecutionContext(string traceId, object? input)
    {
        TraceId = traceId;
        Input = input;
    }

    public async Task<T> TrackStepAsync<T>(string name, object? input, Func<Task<T>> step)
        => await TrackStepAsync(name, input, _ => step());

    public async Task<T> TrackStepAsync<T>(string name, object? input, Func<StepHandle, Task<T>> step)
    {
        if (_mockOutputs != null && _strictMockMode && (!_mockOutputs.TryGetValue(name, out var strictQueue) || strictQueue.Count == 0))
            throw new InvalidOperationException(
                $"Replay mode: no mock output for step '{name}'. " +
                $"Available steps: {string.Join(", ", _mockOutputs.Keys)}. " +
                "This would have made a real LLM call.");

        if (_mockOutputs != null && _mockOutputs.TryGetValue(name, out var queue) && queue.Count > 0)
        {
            var recorded = queue.Dequeue();
            var mock = recorded.Output;
            T result;
            if (mock is T typed) result = typed;
            else if (mock != null)
            {
                var json = JsonSerializer.Serialize(mock);
                result = JsonSerializer.Deserialize<T>(json)!;
            }
            else result = default!;

            // Keep the recorded type so ToolCalled and snapshot types behave the same as in the original run.
            var mocked = new Step(name, null) { Input = input, Type = recorded.Type };
            mocked.Metadata["_source"] = "replay";
            mocked.Complete(result);
            _steps.Add(mocked);
            return result;
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
        _mockOutputs = new Dictionary<string, Queue<StepData>>();
        _strictMockMode = strict;

        if (trace.Steps.Count == 0)
            throw new InvalidOperationException(
                $"Replay trace '{trace.TraceId}' has no steps. Cannot mock LLM calls. " +
                "Ensure the trace was recorded with steps before using it for replay.");

        foreach (var step in trace.Steps)
        {
            if (!_mockOutputs.ContainsKey(step.Name))
                _mockOutputs[step.Name] = new Queue<StepData>();
            _mockOutputs[step.Name].Enqueue(step);
        }
    }
}
