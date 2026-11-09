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
        handle.set_meta("type", "llm")
        handle.set_meta("model", "claude-opus-4-6")
        return response.text

    return await ctx.track_step_async("call-llm", ctx.input, llm_call)
```

## Synchronous agents

If your agent is plain synchronous code, use `run` and `track_step`. They take ordinary functions and don't start an event loop:

```python
def agent(ctx: VevalExecutionContext) -> str:
    def llm_call(handle):
        handle.set_meta("type", "llm")
        return call_my_llm_sync(ctx.input)

    return ctx.track_step("call-llm", ctx.input, llm_call)

output = sdk.run("my-agent", agent, input="Hello")
```

`get_trace`, `replay` and `run_scenario` are synchronous too, for tests written without asyncio. Call them from synchronous code; inside a running event loop, await the `_async` versions.

## Side effects during replay

Replay serves recorded outputs only to code inside a step. Check `ctx.is_replay` before work outside a step that must not run again, such as sending an email. It is true in every replay, mocked or live:

```python
if not ctx.is_replay:
    send_email(customer, reply)
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

A trace-backed item (`ScenarioItem(trace_id=...)`) replays live: steps recorded with `set_meta("type", "llm")` call the model with your current prompt, so a prompt regression fails the item, and every other step returns its recorded output, so tools never reach a real system. Set `mock_model_steps=True` on the item, or pass `ScenarioOptions(mock_model_steps=True)` to `run_scenario_async` for every item (including ones loaded from the dashboard), for a free, fully mocked replay that checks code changes but can't see prompt changes. `VevalTestSdk` always replays trace-backed items mocked. `ReplayOptions(live_model_steps=True)` does the same for `replay_async`.

When a live replay leaves its recording (a step recorded as `tool` called with an input the recording doesn't have, or a step with no recording left), it stops at that step and is inconclusive: `outcome` is `"inconclusive"`, `passed` is False, assertions aren't evaluated, and `inconclusive` reports the step and both inputs. In a scenario, any inconclusive item fails the run unless `ScenarioOptions(max_inconclusive_rate=...)` allows a share of them; `summary` on replay, item and run results says it in one line. `ReplayOptions(step_rules={...})` matches chosen steps on name only (`ReplayStepRule(match_on_name_only=True)`), ignores input fields (`ignore_input_fields`), or sets `on_divergence` per step; pass them in `ScenarioOptions(step_rules=...)` to `run_scenario_async` or set `ScenarioItem.step_rules` for scenarios. A model step the recording never contained runs live only if the call site marks it: `ctx.track_step_async(name, input, step, StepOptions(type=StepType.LLM))`.

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

Each call gets the recorded step with the same name and an identical input, or else the next unused one in recorded order, reported as diverged in `replay.fidelity`. A step with no recording raises instead of calling live, and a recorded step the agent never calls fails the replay.

To test failure paths, add faults: replay serves a timeout, an HTTP error, a rate limit, a malformed output or your own exception in place of a step's recorded output, so retries, fallbacks and guards run.

```python
replay = sdk.replay(trace, my_agent, ReplayOptions(
    mock_llm_responses=True,
    faults=[ReplayFault.timeout("search_flights").times(1)],  # fail once, then serve the recording
    assertions=[TraceAssert.max_attempts("search_flights", 2)],
))
```

A fault that never fires fails the replay, and `replay.faults` says how many times each fired. After a fault, a mocked replay serves only calls that still match their recording; any other call stops it as inconclusive (reason `"after_fault"`). `ReplayFault.returns(step, value)` serves a value of your choice. A fault replaces the whole step, so it tests code around the step; if your retry catches your client's exception type (e.g. `anthropic.RateLimitError`), inject that type with `ReplayFault.throw`. Scenarios take faults too: `ScenarioOptions(faults=...)` and `ScenarioItem(faults=...)`.

Store a known-good run — every step with its input and output — and detect when a new run drifts from it: a changed prompt or tool argument, a step added, dropped, repeated, or reordered:

```python
# Save a baseline from a recorded trace. The baseline and the trace are pinned, so retention doesn't delete
# them. Pins are limited per plan; pass SaveSnapshotOptions(pinned=False) to save one that expires.
asyncio.run(sdk.save_snapshot_async("my-baseline", "tr_abc123"))

# In tests: an assertion like any other. A missing baseline fails; it never passes silently.
replay = asyncio.run(sdk.replay_async(trace, my_agent, ReplayOptions(
    mock_llm_responses=True,
    assertions=[TraceAssert.matches_snapshot(sdk, "my-baseline")],
    compare_with_recording=SnapshotOptions(),  # also fail if steps/inputs drift from the trace
)))

# Or compare directly, and record the result in the dashboard.
baseline = asyncio.run(sdk.get_snapshot_async("my-baseline"))
diff = asyncio.run(sdk.compare_snapshot_async("my-baseline", baseline, ctx))
if diff.has_changes:
    print(diff.summary())  # every change, with a line diff of changed inputs
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
| `api_key` | `str` | `""` | Your Veval API key — it also determines the workspace |
| `project_id` | `str` | `""` | Deprecated and ignored; will be removed |
| `raise_on_send_failure` | `bool` | `False` | Raise `VevalSendError` when a trace or scenario run can't be stored. Off by default: the failure is logged as a warning on the `veval` logger with the HTTP status. Turn it on in tests and CI |

### `VevalSdk`

| Method | Description |
|---|---|
| `run_async(name, callback, input)` | Run agent and send trace |
| `run(name, callback, input)` | Synchronous `run_async`, for a plain-function agent |
| `get_trace_async(trace_id)` / `get_trace(trace_id)` | Fetch a recorded trace |
| `save_snapshot_async(name, trace_id_or_ctx, options=None)` | Store a named, pinned baseline (`SaveSnapshotOptions.pinned`, default True); raises `VevalRequestError` (409 at the plan's pin limit) |
| `get_snapshot_async(name)` | Load the latest stored baseline |
| `load_snapshot_async(trace_id)` | Build a snapshot from a trace (not pinned) |
| `compare_snapshot_async(name, snapshot, ctx, options)` | Diff a run against a baseline and record it |
| `replay_async(trace, callback, options)` / `replay(...)` | Replay trace with mocked outputs |
| `run_scenario_async(name, agent, assertions, items)` / `run_scenario(...)` | Run a test scenario |

### `TraceAssert` built-ins

| Assertion | Description |
|---|---|
| `no_errors()` | All steps must succeed (injected faults aside) |
| `max_steps(n)` | Total step count must not exceed `n` |
| `step_exists(name)` | A step with this name must appear |
| `max_cost(usd)` | Total cost must not exceed `usd` |
| `max_duration(ms)` | Total step duration must not exceed `ms` |
| `output_contains(text)` | At least one step output must contain `text` |
| `tool_called(name)` | A tool step with this name must appear |
| `max_attempts(name, n)` | At most `n` calls of the step after its first failure (an error or an injected fault) |

Custom assertions implement `async ITraceAssertion.evaluate_async(ctx) -> Optional[str]` — return `None` to pass or an error string to fail.

## Requirements

- Python 3.10+
- No third-party dependencies
