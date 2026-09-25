namespace Veval.Sdk.Internal;

/// <summary>Wire shapes for snapshot calls, shared by <see cref="VevalSdk"/> and <see cref="VevalTestSdk"/>.</summary>
internal static class SnapshotPayloads
{
    public static object Step(SnapshotStep s) => new { name = s.Name, type = s.Type, input = s.Input, output = s.Output };

    public static object SaveFromTrace(string name, string traceId) => new { name, trace_id = traceId };

    public static object SaveFromContext(string name, VevalExecutionContext ctx) => new
    {
        name,
        source_trace_id = ctx.TraceId,
        steps = SnapshotData.FromContext(ctx).Steps.Select(Step),
    };

    /// <summary>A scenario-run body the dashboard renders as a step-by-step snapshot diff.</summary>
    public static object Run(string snapshotName, SnapshotDiff diff) => new
    {
        passed     = !diff.HasChanges,
        pass_count = diff.HasChanges ? 0 : 1,
        fail_count = diff.HasChanges ? 1 : 0,
        results    = new[]
        {
            new
            {
                name      = snapshotName,
                passed    = !diff.HasChanges,
                type      = "snapshot",
                expected  = diff.ExpectedSteps.Select(Step),
                actual    = diff.ActualSteps.Select(Step),
                alignment = diff.Alignment.Select(r => new
                {
                    kind = r.Kind, step_name = r.StepName,
                    expected_index = r.ExpectedIndex, actual_index = r.ActualIndex,
                    input_changed = r.InputChanged, output_changed = r.OutputChanged,
                }),
                changes   = diff.Changes.Select(c => new
                {
                    kind = c.Kind.ToString(), step_name = c.StepName, occurrence = c.Occurrence,
                    expected_index = c.ExpectedIndex, actual_index = c.ActualIndex, detail = c.Detail,
                }),
                failures  = diff.Changes.Select(c => c.ToString()),
            },
        },
    };
}

internal static class ReplayRecordingCheck
{
    /// <summary>Runs <see cref="ReplayOptions.CompareWithRecording"/> if requested, adding a failure on drift.</summary>
    public static SnapshotDiff? Evaluate(TraceData trace, VevalExecutionContext ctx, ReplayOptions options, List<string> failures)
    {
        if (options.CompareWithRecording is not { } snapshotOptions) return null;

        var diff = SnapshotComparer.Compare(SnapshotData.FromTrace(trace), ctx, snapshotOptions);
        if (diff.HasChanges)
            failures.Add("Replay drifted from its recording — " + diff.Summary($"recording {trace.TraceId}"));
        return diff;
    }
}
