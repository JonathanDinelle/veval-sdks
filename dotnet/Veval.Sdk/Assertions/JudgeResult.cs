using System.Text.Json.Serialization;

namespace Veval.Sdk.Assertions;

public class JudgeResult
{
    [JsonPropertyName("score")]     public double Score { get; set; }
    [JsonPropertyName("passed")]    public bool Passed { get; set; }
    [JsonPropertyName("reasoning")] public string Reasoning { get; set; } = string.Empty;
}
