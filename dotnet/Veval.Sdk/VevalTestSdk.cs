using Veval.Sdk.Assertions;
using Veval.Sdk.Internal;

namespace Veval.Sdk;

/// <summary>
/// Test double for IVevalSdk. Guarantees no live LLM calls — unlike AgentSdk which can
/// silently fall through to live LLM if a trace has empty steps.
/// Always reports to the dashboard so runs count toward quota.
///
/// Usage:
///   var sdk = new VevalTestSdk(options).WithReplay(trace);
///   // inject into your service, call normally.
/// </summary>
public class VevalTestSdk : IVevalSdk
{
    private readonly VevalHttpClient _reportingClient;
    private TraceData? _replayTrace;
    private string _lastStatus = "success";
    private string? _lastError;

    public VevalTestSdk(VevalOptions options)
    {
        _reportingClient = new VevalHttpClient(options.ApiKey, options.Endpoint);
    }

    public VevalTestSdk WithReplay(TraceData trace)
    {
        _replayTrace = trace;
        return this;
    }

    public async Task<T> RunAsync<T>(string agentName, Func<VevalExecutionContext, Task<T>> callback, object? input = null)
    {
        var ctx = new VevalExecutionContext($"tr_{Guid.NewGuid():N}", input);
        if (_replayTrace != null)
            ctx.LoadMockOutputs(_replayTrace);

        var startedAt = DateTime.UtcNow;
        object? output = null;
        string status = "success";
        string? error = null;

        try
        {
            output = await callback(ctx);
            _lastStatus = "success";
            _lastError = null;
        }
        catch (Exception ex)
        {
            status = "error";
            error = ex.Message;
            _lastStatus = "error";
            _lastError = ex.Message;
            throw;
        }

        var completedAt = DateTime.UtcNow;
        var extraMeta = new Dictionary<string, object> { ["replay"] = true };
        if (_replayTrace != null)
            extraMeta["source_trace_id"] = _replayTrace.TraceId;

        var payload = VevalSdk.BuildPayload(
            ctx.TraceId, agentName, ctx,
            input, output, status, error,
            startedAt, completedAt,
            extraMeta);
        await _reportingClient.SendTraceAsync(payload);

        return (T)output!;
    }

    public string LastStatus => _lastStatus;
    public string? LastError => _lastError;

    public Task<TraceData?> GetTraceAsync(string traceId) =>
        Task.FromResult(_replayTrace?.TraceId == traceId ? _replayTrace : null);

    public Task<SnapshotData?> LoadSnapshotAsync(string traceId)
    {
        var trace = _replayTrace?.TraceId == traceId ? _replayTrace : null;
        return Task.FromResult(trace == null ? null : SnapshotData.FromTrace(trace));
    }

    public async Task<SnapshotDiff> CompareSnapshotAsync(string snapshotName, SnapshotData snapshot, VevalExecutionContext ctx)
    {
        var diff   = SnapshotComparer.Compare(snapshot, ctx);
        var actual = SnapshotData.FromContext(ctx);
        await _reportingClient.PostScenarioRunAsync(snapshotName, new
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
        string status = "success";
        string? error = null;
        object? output = null;

        try { output = await callback(ctx); }
        catch (Exception ex) { status = "error"; error = ex.Message; }

        var failures = new List<string>();
        if (error != null) failures.Add($"Replay threw exception: {error}");
        foreach (var a in options.Assertions)
        {
            var f = a.Evaluate(ctx);
            if (f != null) failures.Add(f);
        }

        return new ReplayResult
        {
            Failures = failures,
            ReplayedContext = ctx,
            Output = output,
            StartedAt = startedAt,
            CompletedAt = DateTime.UtcNow,
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
        var scenarioResult = new ScenarioRunResult();
        var resolvedItems = items ?? Array.Empty<ScenarioItem>();

        foreach (var item in resolvedItems)
        {
            var effectiveAssertions = scenarioAssertions.Concat(item.Assertions).ToArray();
            var itemResult = new ItemRunResult { Item = item };

            if (item.TraceId != null)
            {
                var trace = await GetTraceAsync(item.TraceId);
                if (trace == null)
                {
                    itemResult.Failures.Add($"trace '{item.TraceId}' not found — call WithReplay(trace) before RunScenarioAsync");
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

                    var replayCtx = replayResult.ReplayedContext!;
                    var payload = VevalSdk.BuildPayload(
                        replayCtx.TraceId, scenarioName, replayCtx,
                        trace.Input, replayResult.Output,
                        replayResult.Status, replayResult.Error,
                        replayResult.StartedAt, replayResult.CompletedAt,
                        extraMeta: new Dictionary<string, object>
                        {
                            ["replay"] = true,
                            ["source_trace_id"] = item.TraceId,
                        });
                    await _reportingClient.SendTraceAsync(payload);
                }
            }
            else if (item.Input != null)
            {
                var ctx = new VevalExecutionContext($"tr_{Guid.NewGuid():N}", item.Input);
                try { await agent(ctx); }
                catch (Exception ex) { itemResult.Failures.Add($"Agent threw exception: {ex.Message}"); }

                foreach (var a in effectiveAssertions)
                {
                    var f = a.Evaluate(ctx);
                    if (f != null) itemResult.Failures.Add(f);
                }
                itemResult.Context = ctx;
            }
            else
            {
                itemResult.Failures.Add("ScenarioItem must have either TraceId or Input");
            }

            scenarioResult.Results.Add(itemResult);
        }

        await _reportingClient.PostScenarioRunAsync(scenarioName, new
        {
            passed = scenarioResult.Passed,
            pass_count = scenarioResult.PassCount,
            fail_count = scenarioResult.FailCount,
            results = scenarioResult.Results.Select(r => new
            {
                name = r.Item.Name ?? r.Item.TraceId ?? "item",
                passed = r.Passed,
                failures = r.Failures,
            }),
        });

        return scenarioResult;
    }

    public void Dispose() => _reportingClient.Dispose();
}
