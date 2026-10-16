namespace Veval.Sdk.Internal;

/// <summary>
/// Reports one scenario run. Item traces are sent without throwing, so one failed send can't abort the
/// remaining items; each failure is counted, and in strict mode <see cref="CompleteAsync"/> throws once,
/// after the run is posted.
/// </summary>
internal sealed class ScenarioReport
{
    private readonly VevalHttpClient _client;
    private readonly string _scenarioName;
    private int _undelivered;

    public ScenarioReport(VevalHttpClient client, string scenarioName)
    {
        _client = client;
        _scenarioName = scenarioName;
    }

    public async Task SendTraceAsync(object tracePayload)
    {
        if (!await _client.SendTraceAsync(tracePayload, throwOnFailure: false)) _undelivered++;
    }

    /// <summary>Posts the run for trend tracking. Throws only in strict mode (ThrowOnSendFailure).</summary>
    /// <param name="unnamedItem">The name posted for an item with neither a Name nor a TraceId.</param>
    public async Task CompleteAsync(ScenarioRunResult result, string unnamedItem)
    {
        await _client.PostScenarioRunAsync(_scenarioName, new
        {
            passed = result.Passed,
            pass_count = result.PassCount,
            fail_count = result.FailCount,
            results = result.Results.Select(r => new
            {
                name = r.Item.Name ?? r.Item.TraceId ?? unnamedItem,
                passed = r.Passed,
                failures = r.Failures,
                judgments = r.Context?.Judgments ?? (IReadOnlyList<JudgeRecord>)Array.Empty<JudgeRecord>(),
            }),
        });

        if (_undelivered > 0 && _client.ThrowOnSendFailure)
            throw new VevalSendException(
                $"Veval: {_undelivered} trace(s) from scenario '{_scenarioName}' could not be stored; see the warnings above",
                null);
    }
}
