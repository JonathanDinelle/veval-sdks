using System.Text.Json;

namespace Veval.Sdk;

public class VevalExecutionContext
{
    public string TraceId { get; }
    public object? Input { get; }

    private readonly List<Step> _steps = new();
    private readonly Dictionary<string, object> _metadata = new();
    private Dictionary<string, Queue<object?>>? _mockOutputs;
    private bool _strictMockMode;

    internal IReadOnlyList<Step> Steps => _steps;
    internal IReadOnlyDictionary<string, object> TraceMeta => _metadata;

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
            var mock = queue.Dequeue();
            T result;
            if (mock is T typed) result = typed;
            else if (mock != null)
            {
                var json = JsonSerializer.Serialize(mock);
                result = JsonSerializer.Deserialize<T>(json)!;
            }
            else result = default!;

            var mocked = new Step(name, null) { Input = input };
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

    internal void LoadMockOutputs(TraceData trace, bool strict = true)
    {
        _mockOutputs = new Dictionary<string, Queue<object?>>();
        _strictMockMode = strict;

        if (trace.Steps.Count == 0)
            throw new InvalidOperationException(
                $"Replay trace '{trace.TraceId}' has no steps. Cannot mock LLM calls. " +
                "Ensure the trace was recorded with steps before using it for replay.");

        foreach (var step in trace.Steps)
        {
            if (!_mockOutputs.ContainsKey(step.Name))
                _mockOutputs[step.Name] = new Queue<object?>();
            _mockOutputs[step.Name].Enqueue(step.Output);
        }
    }
}
