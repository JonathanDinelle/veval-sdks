using System.Text.Json.Serialization;

namespace Veval.Sdk.Internal;

internal class ScenarioItemDto
{
    [JsonPropertyName("id")]       public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")]     public string? Name { get; set; }
    [JsonPropertyName("trace_id")] public string? TraceId { get; set; }
    [JsonPropertyName("input")]    public object? Input { get; set; }
}

internal class ScenarioItemsResponse
{
    [JsonPropertyName("items")] public List<ScenarioItemDto> Items { get; set; } = new();
}
