namespace Veval.Sdk;

public class Step
{
    public string StepId { get; } = Guid.NewGuid().ToString();
    public string? ParentStepId { get; }
    public string Name { get; }
    public string Type { get; set; } = "custom";
    public object? Input { get; set; }
    public object? Output { get; set; }
    public string Status { get; private set; } = "running";
    public string? Error { get; private set; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; private set; }
    public int? DurationMs { get; private set; }
    public decimal? CostUsd { get; set; }
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public string? Model { get; set; }
    public Dictionary<string, object> Metadata { get; } = new();

    private readonly List<Step> _children = new();
    public IReadOnlyList<Step> Children => _children;

    public Step(string name, string? parentStepId)
    {
        Name = name;
        ParentStepId = parentStepId;
    }

    public void Complete(object? output = null)
    {
        Output = output;
        Status = "success";
        CompletedAt = DateTime.UtcNow;
        DurationMs = (int)(CompletedAt.Value - StartedAt).TotalMilliseconds;
    }

    public void Fail(string error)
    {
        Error = error;
        Status = "error";
        CompletedAt = DateTime.UtcNow;
        DurationMs = (int)(CompletedAt.Value - StartedAt).TotalMilliseconds;
    }

    public Step CreateStep(string name)
    {
        var child = new Step(name, StepId);
        _children.Add(child);
        return child;
    }

    public void SetMetadata(string key, object value) => Metadata[key] = value;
}
