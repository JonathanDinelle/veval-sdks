namespace Veval.Sdk.Internal;

/// <summary>Runs one replay of a recorded trace. Shared by <see cref="VevalSdk"/> and <see cref="VevalTestSdk"/>.</summary>
internal static class ReplayRunner
{
    public static async Task<ReplayResult> RunAsync<T>(
        TraceData trace, Func<VevalExecutionContext, Task<T>> callback, ReplayOptions options)
    {
        if (options.Faults.Count > 0 && !options.MockLlmResponses && !options.LiveModelSteps)
            throw new ArgumentException(
                "ReplayOptions.Faults needs a mocked or live replay: set MockLlmResponses or LiveModelSteps.", nameof(options));

        var ctx = new VevalExecutionContext($"tr_{Guid.NewGuid():N}", trace.Input) { IsReplay = true };
        if (options.LiveModelSteps)
            ctx.LoadMockOutputs(trace, liveModelSteps: true, options: options);
        else if (options.MockLlmResponses)
            ctx.LoadMockOutputs(trace, options: options);

        var startedAt = DateTime.UtcNow;
        object? output = null;
        string status = "success";
        string? error = null;

        try
        {
            output = await callback(ctx);
        }
        catch (Exception ex)
        {
            status = "error";
            error = ex.Message;
        }

        var completedAt = DateTime.UtcNow;
        var failures = new List<string>();
        SnapshotDiff? recordingDiff = null;

        // A stopped live replay can't back up a verdict, so its assertions are not evaluated. Whatever the agent did
        // after the stop (caught it, threw something else) is a consequence of the stop, not a finding.
        if (ctx.ReplayFidelity?.Stop is { } stop)
        {
            status = "inconclusive";
            error = stop.Message;
        }
        else
        {
            if (error != null) failures.Add($"Replay threw exception: {error}");
            if (error == null && ctx.ReplayFidelity?.Failure(trace.TraceId, options.LiveModelSteps) is { } bindingFailure)
                failures.Add(bindingFailure);
            if (ctx.ReplayFidelity is { } fidelity)
                failures.AddRange(fidelity.UnfiredFaultFailures(name => ctx.Steps.Count(s => s.Name == name)));
            foreach (var assertion in options.Assertions)
            {
                var failure = await assertion.EvaluateAsync(ctx);
                if (failure != null) failures.Add(failure);
            }
            recordingDiff = ReplayRecordingCheck.Evaluate(trace, ctx, options, failures);
        }

        return new ReplayResult
        {
            Failures = failures,
            Inconclusive = ctx.ReplayFidelity?.Stop,
            ReplayedContext = ctx,
            Output = output,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Status = status,
            Error = error,
            RecordingDiff = recordingDiff,
            Fidelity = ctx.ReplayFidelity,
        };
    }
}
