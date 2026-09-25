# Veval SDK

Observability, replay testing, and snapshot testing for AI agents. Add a few lines of code to trace every step your agent takes, then debug, replay, and test in production-like conditions.

This is the C# SDK. Node and Python SDKs share the same concepts and wire format.

## Installation

```bash
dotnet add package Veval.Sdk
```

## Quick Start

```csharp
using Veval.Sdk;

using var veval = new VevalSdk(new VevalOptions { ApiKey = "vk_your_api_key" });

var answer = await veval.RunAsync("my-agent", async ctx =>
{
    return await ctx.TrackStepAsync("call-llm", "What is the capital of France?", async step =>
    {
        var response = await CallYourLlm("What is the capital of France?");

        step.SetMeta("type", "llm");
        step.SetMeta("model", "claude-opus-5");
        step.SetMeta("tokens_in", 12);
        step.SetMeta("tokens_out", 8);
        step.SetMeta("cost_usd", 0.0003m);
        return response;
    });
});
```

That's it. The trace is sent to Veval, where you can inspect it, replay it, and test against it.

## Core Concepts

### VevalSdk

The main entry point. Create one instance and reuse it across your application.

```csharp
var veval = new VevalSdk(new VevalOptions
{
    ApiKey = "vk_...",   // Required. Your Veval API key — it also determines the workspace.
});
```

`VevalSdk` implements `IDisposable` — dispose it when your application shuts down. Depend on `IVevalSdk` in your own classes so tests can pass in a `VevalTestSdk`.

### RunAsync

Wraps your agent logic, captures timing, and sends the trace.

```csharp
var result = await veval.RunAsync("agent-name", async ctx =>
{
    // Your agent logic here. Use ctx to track steps.
    return new MyOutput { ... };
}, input: new { query = "user question" });
```

- Records start/end time, duration, and status
- On success: sends the trace with `status: "success"`
- On exception: sends the trace with `status: "error"` and the error message, then re-throws
- Never swallows your exceptions, and never throws on network failures to Veval

### VevalExecutionContext

Passed into your callback. Use it to track steps and attach metadata.

```csharp
ctx.SetMetadata("user_id", "u_123");   // trace-level metadata
Console.WriteLine(ctx.TraceId);         // e.g. for logging
var input = ctx.Input;                  // the input passed to RunAsync
var steps = ctx.Steps;                  // steps recorded so far (read-only)
```

### Steps

Steps are the building blocks of a trace: each operation your agent performs. `TrackStepAsync` runs your code, records its input, output, and duration, and marks the step as failed if it throws (the exception still propagates).

```csharp
var docs = await ctx.TrackStepAsync("retrieve-documents", searchQuery, async step =>
{
    step.SetMeta("type", "tool");            // "llm", "tool", "retrieval", "custom" (default)
    return await vectorDb.Search(searchQuery);
});
```

For steps that don't need metadata, pass a plain function:

```csharp
var intent = await ctx.TrackStepAsync("classify", message, () => ClassifyAsync(message));
```

`step.SetMeta` recognizes `type`, `model`, `tokens_in`, `tokens_out`, and `cost_usd`; any other key is stored as step metadata:

```csharp
step.SetMeta("provider", "anthropic");
step.SetMeta("retry_count", 2);
```

Track your agent's steps with the input you actually send — the full prompt for an LLM call, the arguments for a tool. Replay, snapshots, and the judge all work from those inputs.

## Replay Testing

Replay a recorded trace through your real agent code with the recorded outputs served for every step — no LLM or API calls, deterministic results.

```csharp
using Veval.Sdk.Assertions;

var trace = await veval.GetTraceAsync("tr_abc123");

var testSdk = new VevalTestSdk(new VevalOptions { ApiKey = "vk_..." });
var agent = new MyAgent(testSdk);   // your agent, taking IVevalSdk

var result = await testSdk.ReplayAsync(trace!, agent.ExecuteAsync, new ReplayOptions
{
    MockLlmResponses = true,
    Assertions =
    [
        TraceAssert.NoErrors(),
        TraceAssert.MaxSteps(10),
        TraceAssert.StepExists("call-llm"),
        TraceAssert.OutputContains("expected text"),
    ],
    // Also fail if the replay's steps or inputs drift from the recording.
    CompareWithRecording = new SnapshotOptions(),
});

if (!result.Passed)
    foreach (var failure in result.Failures)
        Console.WriteLine($"FAIL: {failure}");
```

In replay, `TrackStepAsync` returns the recorded output instead of running your code. A step with no recorded output throws rather than silently making a live call. To replay your production entry point unchanged, use `new VevalTestSdk(options).WithReplay(trace)` and call your agent as usual.

### Built-in Assertions

| Assertion | Description |
|-----------|-------------|
| `TraceAssert.NoErrors()` | No step has error status |
| `TraceAssert.MaxSteps(n)` | Total step count <= n |
| `TraceAssert.StepExists("name")` | A step with this name exists |
| `TraceAssert.ToolCalled("name")` | A step of type `tool` with this name exists |
| `TraceAssert.MaxCost(decimal)` | Total cost across all steps <= threshold |
| `TraceAssert.MaxDuration(ms)` | Total duration across all steps <= threshold |
| `TraceAssert.OutputContains("text")` | At least one step output contains the text |
| `TraceAssert.Judge(veval, criteria, options?)` | An LLM judge scores the output against a plain-English rubric |
| `TraceAssert.MatchesSnapshot(...)` | Steps and step inputs match a stored baseline |

In tests, `testSdk.WithJudgeMock(criteria, passed, score, reasoning)` makes `Judge` deterministic and free; without a mock the test SDK throws instead of making a billed call.

### Custom Assertions

Implement `ITraceAssertion` for domain-specific checks. Return `null` to pass, or a failure message:

```csharp
public class NoSecretsInPrompts : ITraceAssertion
{
    public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
    {
        var leaked = ctx.Steps.FirstOrDefault(s => s.Input?.ToString()?.Contains("sk-") == true);
        return Task.FromResult(leaked is null ? null : $"Step '{leaked.Name}' sent a secret");
    }
}
```

## Scenarios

Run a named set of test cases — fixed inputs (live) or recorded trace IDs (replayed) — against shared and per-item assertions. Results are recorded in the dashboard.

```csharp
var result = await veval.RunScenarioAsync(
    scenarioName: "support-quality",
    agent: agent.ExecuteAsync,
    scenarioAssertions: [TraceAssert.NoErrors(), TraceAssert.Judge(veval, "The reply is polite and on-topic.")],
    items:
    [
        new ScenarioItem { Name = "refund request", Input = "I want a refund" },
        new ScenarioItem { Name = "recorded escalation", TraceId = "tr_abc123" },
    ]);

Console.WriteLine($"{result.PassCount}/{result.Results.Count} passed");
```

## Snapshot Testing

Store a known-good run — every step with its input and output — then detect when a new run drifts from it: a changed prompt or tool argument, a step added, dropped, repeated, or reordered.

```csharp
// Save a baseline from a recorded trace. The trace is pinned, so retention never deletes it.
await veval.SaveSnapshotAsync("my-agent-baseline", "tr_abc123");

// In tests: an assertion like any other. A missing baseline fails; it never passes silently.
var result = await testSdk.ReplayAsync(trace, agent.ExecuteAsync, new ReplayOptions
{
    MockLlmResponses = true,
    Assertions = [TraceAssert.MatchesSnapshot(testSdk, "my-agent-baseline")],
});

// Or compare directly, and record the result in the dashboard.
var baseline = await veval.GetSnapshotAsync("my-agent-baseline");
var diff = await veval.CompareSnapshotAsync("my-agent-baseline", baseline!, ctx);
if (diff.HasChanges)
    Console.WriteLine(diff.Summary());   // every change, with a line diff of changed inputs
```

`SnapshotOptions` controls what's compared: `CompareInputs` (default on), `CompareOutputs` (default off), `IgnoreSteps`, `IgnoreFields` (e.g. `timestamp`), and `Normalize`. For offline CI, register a baseline with `testSdk.WithSnapshot(name, snapshot)`.

## Full Example

```csharp
using Veval.Sdk;

public class SupportAgent(IVevalSdk veval, IMyLlm llm, IKnowledgeBase kb)
{
    public Task<string> AnswerAsync(string message) =>
        veval.RunAsync("support-agent", ctx => ExecuteAsync(ctx), input: message);

    // Takes a context, so tests can pass it to ReplayAsync / RunScenarioAsync directly.
    public async Task<string> ExecuteAsync(VevalExecutionContext ctx)
    {
        var message = ctx.Input?.ToString() ?? "";
        ctx.SetMetadata("channel", "chat");

        var intent = await ctx.TrackStepAsync("classify-intent", message, async step =>
        {
            step.SetMeta("type", "llm");
            step.SetMeta("model", "claude-haiku-4-5");
            return await llm.ClassifyAsync(message);
        });

        var docs = await ctx.TrackStepAsync("retrieve-docs", intent, async step =>
        {
            step.SetMeta("type", "tool");
            return await kb.SearchAsync(intent);
        });

        return await ctx.TrackStepAsync("generate-response", new { intent, docs }, async step =>
        {
            step.SetMeta("type", "llm");
            step.SetMeta("model", "claude-opus-5");
            return await llm.RespondAsync(message, docs);
        });
    }
}
```

## API Reference

### VevalSdk / IVevalSdk

| Method | Description |
|--------|-------------|
| `RunAsync<T>(agentName, callback, input?)` | Run agent logic with automatic tracing |
| `GetTraceAsync(traceId)` | Fetch a trace by ID (returns `TraceData?`) |
| `ReplayAsync<T>(trace, callback, options)` | Replay a trace with recorded outputs and assertions |
| `RunScenarioAsync<T>(name, agent, assertions, items?)` | Run a scenario; omit `items` to load them from the dashboard |
| `JudgeAsync(criteria, ctx, options?)` | Score a run against a rubric (used by `TraceAssert.Judge`) |
| `SaveSnapshotAsync(name, traceId)` / `SaveSnapshotAsync(name, ctx)` | Store a named baseline; saving by trace pins it |
| `GetSnapshotAsync(name)` | Load the latest stored baseline (`null` if none) |
| `CompareSnapshotAsync(name, snapshot, ctx, options?)` | Diff a run against a baseline and record it |
| `LoadSnapshotAsync(traceId)` | Build a snapshot from a trace on the fly (not pinned) |

### VevalTestSdk

Everything above, plus: `WithReplay(trace)`, `WithJudgeMock(criteria, passed, score, reasoning)`, `WithSnapshot(name, snapshot)`, `LastStatus`, `LastError`.

### VevalExecutionContext

| Member | Description |
|--------|-------------|
| `TrackStepAsync(name, input, step => ...)` | Run and record a step |
| `SetMetadata(key, value)` | Attach key-value metadata to the trace |
| `TraceId` | The trace ID for this run |
| `Input` | The input passed to `RunAsync` |
| `Steps` | Steps recorded so far (read-only) |
| `Judgments` | Judge verdicts recorded during this run (read-only) |

### Step (as seen through `ctx.Steps`)

| Member | Description |
|--------|-------------|
| `Name`, `Type` | Step name and category (`"llm"`, `"tool"`, `"custom"`, …) |
| `Input` / `Output` | Step input and output |
| `Status`, `Error` | `"success"` or `"error"`, and the error message |
| `DurationMs` | Step duration |
| `Model`, `TokensIn`, `TokensOut`, `CostUsd` | LLM usage, when set via `SetMeta` |
| `Metadata` | Other key-value metadata |

## License

MIT. See LICENSE for details.
