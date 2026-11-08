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
        await _client.PostScenarioRunAsync(_scenarioName, RunPayload(result, unnamedItem));

        if (_undelivered > 0 && _client.ThrowOnSendFailure)
            throw new VevalSendException(
                $"Veval: {_undelivered} trace(s) from scenario '{_scenarioName}' could not be stored; see the warnings above",
                null);
    }

    /// <summary>The run as posted: snake_case keys, a third item outcome, and the stop of each inconclusive item.</summary>
    internal static object RunPayload(ScenarioRunResult result, string unnamedItem) => new
    {
        passed = result.Passed,
        pass_count = result.PassCount,
        fail_count = result.FailCount,
        inconclusive_count = result.InconclusiveCount,
        max_inconclusive_rate = result.MaxInconclusiveRate,
        summary = result.Summary,
        results = result.Results.Select(r => new
        {
            name = r.Item.Name ?? r.Item.TraceId ?? unnamedItem,
            // Kept for readers that predate outcome: true only when the item passed.
            passed = r.Passed,
            outcome = OutcomeName(r.Outcome),
            replayed = r.Fidelity != null,
            inconclusive = r.Inconclusive is { } stop
                ? new
                {
                    step = stop.Step,
                    step_name = stop.StepName,
                    reason = stop.Reason == ReplayStopReason.Diverged ? "diverged" : "no_recording",
                    recorded_input = stop.RecordedInput,
                    actual_input = stop.ActualInput,
                    message = stop.Message,
                }
                : null,
            summary = r.Summary,
            failures = r.Failures,
            judgments = r.Context?.Judgments ?? (IReadOnlyList<JudgeRecord>)Array.Empty<JudgeRecord>(),
        }),
    };

    private static string OutcomeName(ReplayOutcome outcome) => outcome switch
    {
        ReplayOutcome.Passed => "passed",
        ReplayOutcome.Failed => "failed",
        _ => "inconclusive",
    };
}
