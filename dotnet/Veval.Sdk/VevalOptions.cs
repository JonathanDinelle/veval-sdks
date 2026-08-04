namespace Veval.Sdk;

public class VevalOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Endpoint { get; init; } = "https://api.veval.dev";
    public int FlushIntervalMs { get; set; } = 5000;
    public int FlushBatchSize { get; set; } = 50;
}
