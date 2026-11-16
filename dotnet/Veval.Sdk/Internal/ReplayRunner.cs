namespace Veval.Sdk.Internal;

/// <summary>Runs one replay of a recorded trace. Shared by <see cref="VevalSdk"/> and <see cref="VevalTestSdk"/>.</summary>
internal static class ReplayRunner
{
    public static async Task<ReplayResult> RunAsync<T>(
        TraceData trace, Func<VevalExecutionContext, Task<T>> callback, ReplayOptions options)
    {
        if (options.LiveToolSteps && options.LiveModelSteps)
            throw new ArgumentException(
                "ReplayOptions.LiveToolSteps and LiveModelSteps can't be combined: one runs tools and serves the model from " +
                "the recording, the other the reverse.", nameof(options));
        if (options.Faults.Count > 0 && !options.MockLlmResponses && !options.LiveModelSteps && !options.LiveToolSteps)
            throw new ArgumentException(
                "ReplayOptions.Faults needs a mocked or live replay: set MockLlmResponses, LiveModelSteps or LiveToolSteps.",
                nameof(options));

        var ctx = new VevalExecutionContext($"tr_{Guid.NewGuid():N}", trace.Input) { IsReplay = true };
        if (options.LiveModelSteps)
            ctx.LoadMockOutputs(trace, liveModelSteps: true, options: options);
        else if (options.MockLlmResponses || options.LiveToolSteps)
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

        var toolChanges = ctx.ReplayFidelity?.ToolOutputChanges ?? Array.Empty<ToolOutputCheck>();
        var stop = ctx.ReplayFidelity?.Stop;

        // A stopped replay can't back up a verdict, so its assertions are not evaluated. Whatever the agent did after the
        // stop (caught it, threw something else) is a consequence of the stop, not a finding. A tool output that changed
        // before the stop is a finding: the replay fails on it, and the stop is the change's consequence.
        if (stop != null && toolChanges.Count > 0)
        {
            status = "inconclusive";
            error = stop.Message;
            failures.AddRange(toolChanges.Select(c => c.Message!));
            failures.Add($"The replay then stopped at step {stop.Step} ('{stop.StepName}'), whose input follows from the changed output.");
        }
        else if (stop != null)
        {
            status = "inconclusive";
            error = stop.Message;
        }
        else
        {
            if (error != null) failures.Add($"Replay threw exception: {error}");
            if (error == null && ctx.ReplayFidelity?.Failure(trace.TraceId, options.LiveModelSteps) is { } bindingFailure)
                failures.Add(bindingFailure);
            // A changed tool output is reported even when the agent then threw: the change is likely why it did.
            failures.AddRange(toolChanges.Select(c => c.Message!));
            // Like the binding failure: when the agent threw, the exception already explains why a fault never fired.
            if (error == null && ctx.ReplayFidelity is { } fidelity)
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
            // Failed, not inconclusive, when a tool output changed before the stop.
            Inconclusive = toolChanges.Count > 0 ? null : stop,
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
