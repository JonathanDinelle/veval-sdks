using System.Text.Json.Serialization;

namespace Veval.Sdk;

public class SnapshotStep
{
    [JsonPropertyName("name")]   public string Name { get; set; } = "";
    [JsonPropertyName("type")]   public string? Type { get; set; }
    [JsonPropertyName("input")]  public object? Input { get; set; }
    [JsonPropertyName("output")] public object? Output { get; set; }
}

/// <summary>
/// A known-good run to compare new runs against: every step in order, with the input the agent sent
/// and the output it got back. Stored server-side by name (<see cref="IVevalSdk.SaveSnapshotAsync(string, string)"/>)
/// so it survives trace retention.
/// </summary>
public class SnapshotData
{
    /// <summary>Snapshot format: 1 = legacy, step names only. 2 = full steps with inputs/outputs.</summary>
    public const int CurrentVersion = 2;

    [JsonPropertyName("id")]              public string? Id { get; set; }
    [JsonPropertyName("name")]            public string? Name { get; set; }
    [JsonPropertyName("source_trace_id")] public string? SourceTraceId { get; set; }
    [JsonPropertyName("version")]         public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("steps")]           public List<SnapshotStep> Steps { get; set; } = new();
    [JsonPropertyName("created_at")]      public DateTime? CreatedAt { get; set; }

    // Legacy structural fields, still populated for older dashboards and v1 snapshots.
    [JsonPropertyName("step_names")] public List<string> StepNames { get; set; } = new();
    [JsonPropertyName("step_order")] public List<string> StepOrder { get; set; } = new();
    [JsonPropertyName("step_count")] public int StepCount { get; set; }

    /// <summary>True when the snapshot recorded steps with inputs/outputs, not just names (legacy v1).</summary>
    [JsonIgnore]
    public bool HasStepContent => Steps.Count > 0;

    /// <summary>Steps in run order; for a legacy snapshot, derived from <see cref="StepOrder"/>.</summary>
    internal IReadOnlyList<SnapshotStep> OrderedSteps =>
        Steps.Count > 0 || StepOrder.Count == 0 ? Steps : StepOrder.Select(n => new SnapshotStep { Name = n }).ToList();

    public static SnapshotData FromContext(VevalExecutionContext ctx) =>
        FromSteps(VevalSdk.FlattenSteps(ctx.Steps).Select(s => new SnapshotStep
        {
            Name = s.Name, Type = s.Type, Input = s.Input, Output = s.Output,
        }), sourceTraceId: ctx.TraceId);

    public static SnapshotData FromTrace(TraceData trace) =>
        FromSteps(trace.Steps.Select(s => new SnapshotStep
        {
            Name = s.Name, Type = s.Type, Input = s.Input, Output = s.Output,
        }), sourceTraceId: trace.TraceId);

    private static SnapshotData FromSteps(IEnumerable<SnapshotStep> steps, string? sourceTraceId)
    {
        var list = steps.ToList();
        return new SnapshotData
        {
            Version = CurrentVersion,
            SourceTraceId = sourceTraceId,
            Steps = list,
            StepNames = list.Select(s => s.Name).Distinct().ToList(),
            StepOrder = list.Select(s => s.Name).ToList(),
            StepCount = list.Count,
        };
    }
}
