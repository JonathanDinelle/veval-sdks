# veval-sdk

Python SDK for [Veval](https://veval.dev) — trace, evaluate, and test AI agents.

## Install

```bash
pip install veval-sdk
```

## Quick start

Wrap your agent with `run_async` to send a trace to the Veval dashboard:

```python
import asyncio
from veval import VevalSdk, VevalOptions, VevalExecutionContext

sdk = VevalSdk(VevalOptions(api_key="veval-..."))

async def my_agent(ctx: VevalExecutionContext) -> str:
    result = await ctx.track_step_async(
        name="call-llm",
        input=ctx.input,
        step=lambda: call_my_llm(ctx.input),
    )
    return result

output = asyncio.run(sdk.run_async("my-agent", my_agent, input="Hello"))
```

Every call to `track_step_async` records the step name, input, output, timing, and any metadata you attach.

## Tracking steps

Use the `StepHandle` to attach token counts, cost, and model name:

```python
async def agent(ctx: VevalExecutionContext) -> str:
    async def llm_call(handle):
        response = await call_my_llm(ctx.input)
        handle.set_meta("tokens_in", response.usage.input_tokens)
        handle.set_meta("tokens_out", response.usage.output_tokens)
        handle.set_meta("cost_usd", response.usage.cost)
        handle.set_meta("model", "claude-opus-4-6")
        return response.text

    return await ctx.track_step_async("call-llm", ctx.input, llm_call)
```

## Assertions

Use `TraceAssert` to validate behaviour at the trace level:

```python
from veval import TraceAssert

assertions = [
    TraceAssert.no_errors(),
    TraceAssert.max_steps(10),
    TraceAssert.step_exists("call-llm"),
    TraceAssert.max_cost(0.05),
    TraceAssert.max_duration(30_000),
    TraceAssert.output_contains("success"),
    TraceAssert.tool_called("web-search"),
]
```

## Scenarios

Run a named scenario against a list of test items and assertions:

```python
from veval import ScenarioItem

items = [
    ScenarioItem(name="basic greeting", input="Hello"),
    ScenarioItem(name="edge case", input=""),
]

result = asyncio.run(
    sdk.run_scenario_async("smoke-test", my_agent, assertions, items=items)
)

print(f"Passed: {result.pass_count}/{len(result.results)}")
for r in result.results:
    if not r.passed:
        print(f"  FAIL {r.item.name}: {r.failures}")
```

You can also pull items from the Veval dashboard by omitting `items`:

```python
result = asyncio.run(sdk.run_scenario_async("smoke-test", my_agent, assertions))
```

## Replay and snapshot testing

Load a previously recorded trace and replay it with mocked LLM responses — no real calls, deterministic results:

```python
trace = asyncio.run(sdk.get_trace_async("tr_abc123"))

replay = asyncio.run(sdk.replay_async(
    trace,
    my_agent,
    ReplayOptions(mock_llm_responses=True, assertions=assertions),
))

print("Passed" if replay.passed else replay.failures)
```

Take a snapshot of the current step sequence and compare future runs against it:

```python
snapshot = asyncio.run(sdk.load_snapshot_async("tr_abc123"))

diff = asyncio.run(sdk.compare_snapshot_async("my-snapshot", snapshot, ctx))
if diff.has_changes:
    print("Added:", diff.added_steps)
    print("Removed:", diff.removed_steps)
    print("Order changes:", diff.order_changes)
```

## Test SDK

`VevalTestSdk` is a drop-in replacement that blocks real LLM calls, making it safe to use in unit tests. It still reports to the dashboard.

```python
from veval import VevalTestSdk

sdk = VevalTestSdk(options).with_replay(trace)
output = asyncio.run(sdk.run_async("my-agent", my_agent, input="test input"))
```

## API reference

### `VevalOptions`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `api_key` | `str` | `""` | Your Veval API key |
| `project_id` | `str` | `""` | Optional project scoping |
| `flush_interval_ms` | `int` | `5000` | Batch flush interval |
| `flush_batch_size` | `int` | `50` | Max traces per flush |

### `VevalSdk`

| Method | Description |
|---|---|
| `run_async(name, callback, input)` | Run agent and send trace |
| `get_trace_async(trace_id)` | Fetch a recorded trace |
| `load_snapshot_async(trace_id)` | Load a snapshot from a trace |
| `compare_snapshot_async(name, snapshot, ctx)` | Diff current run against snapshot |
| `replay_async(trace, callback, options)` | Replay trace with mocked outputs |
| `run_scenario_async(name, agent, assertions, items)` | Run a test scenario |

### `TraceAssert` built-ins

| Assertion | Description |
|---|---|
| `no_errors()` | All steps must succeed |
| `max_steps(n)` | Total step count must not exceed `n` |
| `step_exists(name)` | A step with this name must appear |
| `max_cost(usd)` | Total cost must not exceed `usd` |
| `max_duration(ms)` | Total step duration must not exceed `ms` |
| `output_contains(text)` | At least one step output must contain `text` |
| `tool_called(name)` | A tool step with this name must appear |

Custom assertions implement `ITraceAssertion.evaluate(ctx) -> Optional[str]` — return `None` to pass or an error string to fail.

## Requirements

- Python 3.10+
- No third-party dependencies
