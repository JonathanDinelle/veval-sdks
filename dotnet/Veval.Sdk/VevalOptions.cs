namespace Veval.Sdk;

public class VevalOptions
{
    public string ApiKey { get; set; } = string.Empty;
    [Obsolete("Ignored: the API key determines the workspace. Will be removed in a future version.")]
    public string ProjectId { get; set; } = string.Empty;

    // Points at Veval's own hosted API, which enforces billing/quota (test-run limits, judge
    // credits, etc.) — this is not a self-hosted override for SDK consumers, so it's internal
    // rather than a settable public option. VEVAL_INTERNAL_ENDPOINT is an undocumented escape
    // hatch for Veval's own local development against a non-production API instance.
    internal string Endpoint { get; init; } =
        Environment.GetEnvironmentVariable("VEVAL_INTERNAL_ENDPOINT") ?? "https://api.veval.dev";

    public int FlushIntervalMs { get; set; } = 5000;
    public int FlushBatchSize { get; set; } = 50;

    /// <summary>
    /// By default a trace or scenario run that can't be delivered (bad API key, quota, network) is
    /// written to standard error as a warning and never breaks the host app. Set this in tests and CI
    /// to throw <see cref="VevalSendException"/> instead.
    /// </summary>
    public bool ThrowOnSendFailure { get; set; }

    // Where send-failure warnings go; tests replace it to capture them without touching the shared Console.
    internal Action<string> Warn { get; init; } = message => Console.Error.WriteLine(message);
}
