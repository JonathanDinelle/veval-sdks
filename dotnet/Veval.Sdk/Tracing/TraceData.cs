using System.Text.Json.Serialization;

namespace Veval.Sdk;

public class TraceData
{
    [JsonPropertyName("trace_id")]       public string TraceId { get; set; } = string.Empty;
    [JsonPropertyName("agent_name")]     public string AgentName { get; set; } = string.Empty;
    [JsonPropertyName("project_id")]     public string ProjectId { get; set; } = string.Empty;
    [JsonPropertyName("input")]          public object? Input { get; set; }
    [JsonPropertyName("output")]         public object? Output { get; set; }
    [JsonPropertyName("status")]         public string Status { get; set; } = "running";
    [JsonPropertyName("error")]          public string? Error { get; set; }
    [JsonPropertyName("started_at")]     public DateTime StartedAt { get; set; }
    [JsonPropertyName("completed_at")]   public DateTime? CompletedAt { get; set; }
    [JsonPropertyName("duration_ms")]    public int? DurationMs { get; set; }
    [JsonPropertyName("total_cost_usd")] public decimal? TotalCostUsd { get; set; }
    [JsonPropertyName("metadata")]       public Dictionary<string, object> Metadata { get; set; } = new();
    [JsonPropertyName("steps")]          public List<StepData> Steps { get; set; } = new();
}

public class StepData
{
    [JsonPropertyName("step_id")]        public string StepId { get; set; } = string.Empty;
    [JsonPropertyName("parent_step_id")] public string? ParentStepId { get; set; }
    [JsonPropertyName("name")]           public string Name { get; set; } = string.Empty;
    [JsonPropertyName("type")]           public string Type { get; set; } = "custom";
    [JsonPropertyName("input")]          public object? Input { get; set; }
    [JsonPropertyName("output")]         public object? Output { get; set; }
    [JsonPropertyName("status")]         public string Status { get; set; } = string.Empty;
    [JsonPropertyName("error")]          public string? Error { get; set; }
    [JsonPropertyName("started_at")]     public DateTime StartedAt { get; set; }
    [JsonPropertyName("completed_at")]   public DateTime? CompletedAt { get; set; }
    [JsonPropertyName("duration_ms")]    public int? DurationMs { get; set; }
    [JsonPropertyName("cost_usd")]       public decimal? CostUsd { get; set; }
    [JsonPropertyName("tokens_in")]      public int? TokensIn { get; set; }
    [JsonPropertyName("tokens_out")]     public int? TokensOut { get; set; }
    [JsonPropertyName("model")]          public string? Model { get; set; }
    [JsonPropertyName("metadata")]       public Dictionary<string, object> Metadata { get; set; } = new();
}
