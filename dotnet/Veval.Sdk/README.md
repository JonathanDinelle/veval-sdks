# Veval SDK

Observability, replay testing, and snapshot testing for AI agents. Add a few lines of code to trace every step your agent takes, then debug, replay, and test in production-like conditions.

This is the C# reference implementation. SDKs for Python, TypeScript, Go, and Java are auto-generated from the OpenAPI spec via [Speakeasy](https://speakeasyapi.dev/).

## Installation

```bash
dotnet add package Veval.Sdk
```

## Quick Start

```csharp
using Veval.Sdk;

var veval = new AgentSdk(new AgentOptions
{
    ApiKey = "vk_your_api_key",
    ProjectId = "your-project-id",
});

var result = await veval.RunAsync<string>("my-agent", async ctx =>
{
    var step = ctx.Step("call-llm");
    step.Type = "llm";
    step.Input = "What is the capital of France?";

    var answer = await CallYourLlm(step.Input);

    step.Output = answer;
    step.Model = "gpt-4";
    step.TokensIn = 12;
    step.TokensOut = 8;
    step.CostUsd = 0.002m;
    step.Complete(answer);

    return answer;
});
```

That's it. The trace is automatically sent to Veval where you can visualize it, replay it, and set up failure detection.

## Core Concepts

### AgentSdk

The main entry point. Create one instance and reuse it across your application.

```csharp
var veval = new AgentSdk(new AgentOptions
{
    ApiKey = "vk_...",            // Required. Your Veval API key.
    ProjectId = "my-project",     // Required. Groups traces in the dashboard.
    Endpoint = "https://api.veval.dev", // Optional. Self-hosted override.
    FlushIntervalMs = 5000,       // Optional. Batch flush interval.
    FlushBatchSize = 50,          // Optional. Max traces per batch.
});
```

`AgentSdk` implements `IDisposable` — dispose it when your application shuts down.

### RunAsync

Wraps your agent logic, captures timing, and sends the trace.

```csharp
var result = await veval.RunAsync<MyOutput>("agent-name", async ctx =>
{
    // Your agent logic here.
    // Use ctx to track steps.
    return new MyOutput { ... };
}, input: new { query = "user question" });
```

- Automatically records start/end time, duration, and status
- On success: sends trace with `status: "success"`
- On exception: sends trace with `status: "error"` and the error message, then re-throws
- Never swallows exceptions — your error handling stays intact
- Never throws on network failures to Veval — tracing is fire-and-forget

### AgentContext

Passed into your callback. Use it to track steps and attach metadata.

```csharp
// Track a step
var step = ctx.Step("step-name");

// Attach trace-level metadata
ctx.SetMetadata("user_id", "u_123");
ctx.SetMetadata("environment", "production");

// Access the trace ID (e.g., for logging)
Console.WriteLine(ctx.TraceId);
```

### Steps

Steps are the building blocks of a trace. They represent individual operations your agent performs.

```csharp
var step = ctx.Step("retrieve-documents");
step.Type = "retrieval";          // Categorize: "llm", "tool", "retrieval", "custom"
step.Input = searchQuery;

var docs = await vectorDb.Search(searchQuery);

step.Output = docs;
step.Complete();                  // Marks success, records duration
```

If something goes wrong:

```csharp
try
{
    var response = await CallLlm(prompt);
    step.Complete(response);
}
catch (Exception ex)
{
    step.Fail(ex.Message);        // Marks error, records duration
    throw;
}
```

#### Nested Steps

Steps can have children to represent sub-operations:

```csharp
var planStep = ctx.Step("planning");

var analyzeStep = planStep.CreateStep("analyze-input");
analyzeStep.Complete("Input analyzed");

var strategyStep = planStep.CreateStep("choose-strategy");
strategyStep.Complete("Using retrieval approach");

planStep.Complete();
```

#### LLM-Specific Properties

```csharp
step.Model = "claude-sonnet-4-20250514";
step.TokensIn = 1500;
step.TokensOut = 342;
step.CostUsd = 0.0045m;
```

#### Step Metadata

```csharp
step.SetMetadata("provider", "openai");
step.SetMetadata("retry_count", 2);
```

## Replay Testing

Record a production trace, then replay your agent with mocked LLM responses and assertions. Catches regressions when you change agent logic.

```csharp
using Veval.Sdk.Assertions;

// 1. Fetch a known-good trace
var trace = await veval.GetTraceAsync("tr_abc123");

// 2. Replay with assertions
var result = await veval.ReplayAsync<string>(trace, async ctx =>
{
    var step = ctx.Step("call-llm");

    // In replay mode, returns the recorded output instead of calling the real LLM
    var mockedOutput = ctx.GetMockedOutput("call-llm");
    if (mockedOutput != null)
    {
        step.Complete(mockedOutput);
        return mockedOutput.ToString()!;
    }

    // Normal path (non-replay)
    var response = await CallLlm();
    step.Complete(response);
    return response;
}, new ReplayOptions
{
    MockLlmResponses = true,
    Assertions = new ITraceAssertion[]
    {
        TraceAssert.NoErrors(),
        TraceAssert.MaxSteps(10),
        TraceAssert.StepExists("call-llm"),
        TraceAssert.MaxCost(0.05m),
        TraceAssert.MaxDuration(5000),
        TraceAssert.OutputContains("expected text"),
    },
});

// 3. Check results
if (result.Passed)
    Console.WriteLine("Replay passed!");
else
    foreach (var failure in result.Failures)
        Console.WriteLine($"FAIL: {failure}");
```

### Built-in Assertions

| Assertion | Description |
|-----------|-------------|
| `TraceAssert.NoErrors()` | No step has error status |
| `TraceAssert.MaxSteps(n)` | Total step count <= n |
| `TraceAssert.StepExists("name")` | A step with this name exists |
| `TraceAssert.MaxCost(decimal)` | Total cost across all steps <= threshold |
| `TraceAssert.MaxDuration(ms)` | Total duration across all steps <= threshold |
| `TraceAssert.OutputContains("text")` | At least one step output contains the text |

### Custom Assertions

Implement `ITraceAssertion` for domain-specific checks:

```csharp
public class NoHallucinationAssertion : ITraceAssertion
{
    public string? Evaluate(AgentContext ctx)
    {
        // Return null if passed, or a failure message string
        return null;
    }
}
```

## Snapshot Testing

Capture a baseline of your agent's step structure, then detect when it drifts.

```csharp
// After a run, capture the snapshot from the context
var snapshot = SnapshotData.FromContext(ctx);
// snapshot.StepNames  — distinct step names
// snapshot.StepOrder  — ordered list (including repeats)
// snapshot.StepCount  — total count

// Later, compare against a new run
var diff = SnapshotComparer.Compare(snapshot, newCtx);

if (diff.HasChanges)
{
    Console.WriteLine("Added steps: " + string.Join(", ", diff.AddedSteps));
    Console.WriteLine("Removed steps: " + string.Join(", ", diff.RemovedSteps));
    Console.WriteLine("Order changes: " + string.Join(", ", diff.OrderChanges));
}
```

Snapshots can be stored via the Veval API (`POST /api/snapshots`, `GET /api/snapshots/{id}`) for persistent baseline management.

## Full Example

```csharp
using Veval.Sdk;
using Veval.Sdk.Assertions;

using var veval = new AgentSdk(new AgentOptions
{
    ApiKey = Environment.GetEnvironmentVariable("VEVAL_API_KEY")!,
    ProjectId = "customer-support",
});

var result = await veval.RunAsync<string>("support-agent", async ctx =>
{
    ctx.SetMetadata("user_id", "u_456");

    // Step 1: Classify intent
    var classify = ctx.Step("classify-intent");
    classify.Type = "llm";
    classify.Input = ctx.Input;
    var intent = await ClassifyIntent(ctx.Input!.ToString()!);
    classify.Model = "claude-haiku-4-5-20251001";
    classify.TokensIn = 50;
    classify.TokensOut = 10;
    classify.CostUsd = 0.0001m;
    classify.Complete(intent);

    // Step 2: Retrieve knowledge
    var retrieve = ctx.Step("retrieve-docs");
    retrieve.Type = "retrieval";
    var docs = await SearchKnowledgeBase(intent);
    retrieve.Complete(docs);

    // Step 3: Generate response
    var respond = ctx.Step("generate-response");
    respond.Type = "llm";
    respond.Input = new { intent, docs };
    var response = await GenerateResponse(intent, docs);
    respond.Model = "claude-sonnet-4-20250514";
    respond.TokensIn = 2000;
    respond.TokensOut = 500;
    respond.CostUsd = 0.012m;
    respond.Complete(response);

    return response;
}, input: new { message = "I need help with my order" });
```

## API Reference

### AgentSdk

| Method | Description |
|--------|-------------|
| `RunAsync<T>(agentName, callback, input?)` | Run agent logic with automatic tracing |
| `GetTraceAsync(traceId)` | Fetch a trace by ID (returns `TraceData?`) |
| `ReplayAsync<T>(trace, callback, options)` | Replay a trace with assertions |
| `Dispose()` | Clean up HTTP resources |

### AgentContext

| Member | Description |
|--------|-------------|
| `Step(name)` | Create a top-level step |
| `SetMetadata(key, value)` | Attach key-value metadata to the trace |
| `GetMockedOutput(stepName)` | Get mocked output during replay (returns `null` outside replay) |
| `TraceId` | The trace ID for this run |
| `Input` | The input passed to `RunAsync` |

### Step

| Member | Description |
|--------|-------------|
| `Complete(output?)` | Mark step as successful |
| `Fail(error)` | Mark step as failed |
| `CreateStep(name)` | Create a nested child step |
| `SetMetadata(key, value)` | Attach key-value metadata to the step |
| `Type` | Step category: `"llm"`, `"tool"`, `"retrieval"`, `"custom"` |
| `Input` / `Output` | Step input and output data |
| `Model` | LLM model name |
| `TokensIn` / `TokensOut` | Token usage |
| `CostUsd` | Cost in USD |

## License

Proprietary. See LICENSE for details.
