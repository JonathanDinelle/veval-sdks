namespace Veval.Sdk;

public class SnapshotStep
{
    public string Name { get; set; } = "";
    public object? Output { get; set; }
}

public class SnapshotData
{
    public string? Id { get; set; }
    public List<string> StepNames { get; set; } = new();
    public List<string> StepOrder { get; set; } = new();
    public int StepCount { get; set; }
    public List<SnapshotStep> Steps { get; set; } = new();

    public static SnapshotData FromContext(VevalExecutionContext ctx)
    {
        var steps = new List<SnapshotStep>();
        CollectSteps(ctx.Steps, steps);
        return new SnapshotData
        {
            StepNames = steps.Select(s => s.Name).Distinct().ToList(),
            StepOrder  = steps.Select(s => s.Name).ToList(),
            StepCount  = steps.Count,
            Steps      = steps,
        };
    }

    public static SnapshotData FromTrace(TraceData trace)
    {
        var steps = trace.Steps.Select(s => new SnapshotStep { Name = s.Name, Output = s.Output }).ToList();
        return new SnapshotData
        {
            StepNames = steps.Select(s => s.Name).Distinct().ToList(),
            StepOrder  = steps.Select(s => s.Name).ToList(),
            StepCount  = steps.Count,
            Steps      = steps,
        };
    }

    private static void CollectSteps(IReadOnlyList<Step> steps, List<SnapshotStep> result)
    {
        foreach (var s in steps)
        {
            result.Add(new SnapshotStep { Name = s.Name, Output = s.Output });
            CollectSteps(s.Children, result);
        }
    }
}
