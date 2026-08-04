namespace Veval.Sdk;

public class VevalOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;

    // Points at Veval's own hosted API, which enforces billing/quota (test-run limits, judge
    // credits, etc.) — this is not a self-hosted override for SDK consumers, so it's internal
    // rather than a settable public option. VEVAL_INTERNAL_ENDPOINT is an undocumented escape
    // hatch for Veval's own local development against a non-production API instance.
    internal string Endpoint { get; } =
        Environment.GetEnvironmentVariable("VEVAL_INTERNAL_ENDPOINT") ?? "https://api.veval.dev";

    public int FlushIntervalMs { get; set; } = 5000;
    public int FlushBatchSize { get; set; } = 50;
}
