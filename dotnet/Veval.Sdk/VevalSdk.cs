using Veval.Sdk.Assertions;
using Veval.Sdk.Internal;

namespace Veval.Sdk;

public class VevalSdk : IVevalSdk
{
    private readonly VevalOptions _options;
    private readonly VevalHttpClient _client;

    public VevalSdk(VevalOptions options)
    {
        _options = options;
        _client = new VevalHttpClient(options.ApiKey, options.Endpoint);
    }

    public async Task<T> RunAsync<T>(string agentName, Func<VevalExecutionContext, Task<T>> callback, object? input = null)
    {
        var traceId = $"tr_{Guid.NewGuid():N}";
        var ctx = new VevalExecutionContext(traceId, input);
        var startedAt = DateTime.UtcNow;

        try
        {
            var result = await callback(ctx);

            var completedAt = DateTime.UtcNow;
            var payload = BuildPayload(traceId, agentName, ctx, input, result, "success", null, startedAt, completedAt);
            await _client.SendTraceAsync(payload);

            return result;
        }
        catch (Exception ex)
        {
            var completedAt = DateTime.UtcNow;
            var payload = BuildPayload(traceId, agentName, ctx, input, null, "error", ex.Message, startedAt, completedAt);
            await _client.SendTraceAsync(payload);
            throw;
        }
    }

    public async Task<TraceData?> GetTraceAsync(string traceId)
    {
        return await _client.GetTraceAsync(traceId);
    }

    public async Task<SnapshotData?> LoadSnapshotAsync(string traceId)
    {
        var trace = await _client.GetTraceAsync(traceId);
        return trace == null ? null : SnapshotData.FromTrace(trace);
    }

    public async Task<JudgeResult> JudgeAsync(string criteria, VevalExecutionContext ctx, JudgeOptions? options = null)
    {
        var lastStep = ctx.Steps.LastOrDefault();
        var payload = new
        {
            criteria,
            input = lastStep?.Input ?? ctx.Input,
            output = lastStep?.Output,
            model = options?.Model,
        };
        return await _client.JudgeAsync(payload);
    }

    public async Task<SnapshotDiff> CompareSnapshotAsync(string snapshotName, SnapshotData snapshot, VevalExecutionContext ctx)
    {
        var diff    = SnapshotComparer.Compare(snapshot, ctx);
        var actual  = SnapshotData.FromContext(ctx);
        await _client.PostScenarioRunAsync(snapshotName, new
        {
            passed     = !diff.HasChanges,
            pass_count = diff.HasChanges ? 0 : 1,
            fail_count = diff.HasChanges ? 1 : 0,
            results    = new[]
            {
                new
                {
                    name     = snapshotName,
                    passed   = !diff.HasChanges,
                    type     = "snapshot",
                    expected = snapshot.Steps.Select(s => new { s.Name, s.Output }),
                    actual   = actual.Steps.Select(s => new { s.Name, s.Output }),
                    failures = diff.AddedSteps.Select(s => $"added: {s}")
                        .Concat(diff.RemovedSteps.Select(s => $"removed: {s}"))
                        .Concat(diff.OrderChanges),
                }
            },
        });
        return diff;
    }

    public async Task<ReplayResult> ReplayAsync<T>(
        TraceData trace, Func<VevalExecutionContext, Task<T>> callback, ReplayOptions options)
    {
        var ctx = new VevalExecutionContext($"tr_{Guid.NewGuid():N}", trace.Input);
        if (options.MockLlmResponses)
            ctx.LoadMockOutputs(trace);

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
        if (error != null) failures.Add($"Replay threw exception: {error}");
        foreach (var assertion in options.Assertions)
        {
            var failure = await assertion.EvaluateAsync(ctx);
            if (failure != null)
                failures.Add(failure);
        }

        return new ReplayResult
        {
            Failures = failures,
            ReplayedContext = ctx,
            Output = output,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            Status = status,
            Error = error,
        };
    }

    public async Task<ScenarioRunResult> RunScenarioAsync<T>(
        string scenarioName,
        Func<VevalExecutionContext, Task<T>> agent,
        ITraceAssertion[] scenarioAssertions,
        ScenarioItem[]? items = null)
    {
        // Resolve items: inline takes precedence, otherwise fetch from API
        var resolvedItems = items ?? (await _client.GetScenarioItemsAsync(scenarioName))
            .Select(dto => new ScenarioItem { Name = dto.Name, TraceId = dto.TraceId, Input = dto.Input })
            .ToArray();

        var scenarioResult = new ScenarioRunResult();

        foreach (var item in resolvedItems)
        {
            var effectiveAssertions = scenarioAssertions.Concat(item.Assertions).ToArray();
            var itemResult = new ItemRunResult { Item = item };

            if (item.TraceId != null)
            {
                // Trace-backed item: replay with mocked LLM responses
                var trace = await GetTraceAsync(item.TraceId);
                if (trace == null)
                {
                    itemResult.Failures.Add($"trace '{item.TraceId}' not found");
                }
                else
                {
                    var replayResult = await ReplayAsync(trace, agent, new ReplayOptions
                    {
                        MockLlmResponses = true,
                        Assertions = effectiveAssertions,
                    });
                    itemResult.Failures.AddRange(replayResult.Failures);
                    itemResult.Context = replayResult.ReplayedContext;

                    // Record the replay as a trace so it appears in the dashboard
                    var replayCtx = replayResult.ReplayedContext!;
                    var replayPayload = BuildPayload(
                        replayCtx.TraceId, scenarioName, replayCtx,
                        trace.Input, replayResult.Output,
                        replayResult.Status, replayResult.Error,
                        replayResult.StartedAt, replayResult.CompletedAt,
                        extraMeta: new Dictionary<string, object>
                        {
                            ["replay"] = true,
                            ["source_trace_id"] = item.TraceId,
                        });
                    await _client.SendTraceAsync(replayPayload);
                }
            }
            else if (item.Input != null)
            {
                // Synthetic item: live run with real LLM
                var ctx = await RunAndCaptureContextAsync(scenarioName, agent, item.Input);
                foreach (var assertion in effectiveAssertions)
                {
                    var failure = await assertion.EvaluateAsync(ctx);
                    if (failure != null) itemResult.Failures.Add(failure);
                }
                itemResult.Context = ctx;
            }
            else
            {
                itemResult.Failures.Add("ScenarioItem must have either TraceId or Input");
            }

            scenarioResult.Results.Add(itemResult);
        }

        // Post results to API for trend tracking (fire-and-forget)
        await _client.PostScenarioRunAsync(scenarioName, new
        {
            passed = scenarioResult.Passed,
            pass_count = scenarioResult.PassCount,
            fail_count = scenarioResult.FailCount,
            results = scenarioResult.Results.Select(r => new
            {
                name = r.Item.Name ?? r.Item.TraceId ?? "synthetic",
                passed = r.Passed,
                failures = r.Failures,
            }),
        });

        return scenarioResult;
    }

    private async Task<VevalExecutionContext> RunAndCaptureContextAsync<T>(
        string agentName,
        Func<VevalExecutionContext, Task<T>> agent,
        object? input)
    {
        var traceId = $"tr_{Guid.NewGuid():N}";
        var ctx = new VevalExecutionContext(traceId, input);
        var startedAt = DateTime.UtcNow;

        try
        {
            var result = await agent(ctx);
            var completedAt = DateTime.UtcNow;
            var payload = BuildPayload(traceId, agentName, ctx, input, result, "success", null, startedAt, completedAt);
            await _client.SendTraceAsync(payload);
        }
        catch (Exception ex)
        {
            var completedAt = DateTime.UtcNow;
            var payload = BuildPayload(traceId, agentName, ctx, input, null, "error", ex.Message, startedAt, completedAt);
            await _client.SendTraceAsync(payload);
            // intentionally don't rethrow — let assertions detect the error via step statuses
        }

        return ctx;
    }

    internal static object BuildPayload(
        string traceId, string agentName, VevalExecutionContext ctx,
        object? input, object? output, string status, string? error,
        DateTime startedAt, DateTime completedAt,
        Dictionary<string, object>? extraMeta = null)
    {
        var steps = FlattenSteps(ctx.Steps);
        var durationMs = (int)(completedAt - startedAt).TotalMilliseconds;
        var totalCost = steps.Sum(s => s.CostUsd ?? 0);

        Dictionary<string, object> metadata = new(ctx.TraceMeta);
        if (extraMeta != null)
            foreach (var kv in extraMeta) metadata[kv.Key] = kv.Value;

        return new
        {
            trace_id = traceId,
            agent_name = agentName,
            input,
            output,
            status,
            error,
            started_at = startedAt,
            completed_at = completedAt,
            duration_ms = durationMs,
            total_cost_usd = totalCost,
            metadata,
            steps = steps.Select(s => new
            {
                step_id = s.StepId,
                parent_step_id = s.ParentStepId,
                name = s.Name,
                type = s.Type,
                input = s.Input,
                output = s.Output,
                status = s.Status,
                error = s.Error,
                started_at = s.StartedAt,
                completed_at = s.CompletedAt,
                duration_ms = s.DurationMs,
                cost_usd = s.CostUsd,
                tokens_in = s.TokensIn,
                tokens_out = s.TokensOut,
                model = s.Model,
                metadata = s.Metadata,
            }),
        };
    }

    internal static List<Step> FlattenSteps(IReadOnlyList<Step> steps)
    {
        var flat = new List<Step>();
        foreach (var step in steps)
        {
            flat.Add(step);
            flat.AddRange(FlattenSteps(step.Children));
        }
        return flat;
    }

    public void Dispose() => _client.Dispose();
}
