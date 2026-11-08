# @veval/sdk

Node SDK for [Veval](https://veval.dev) — trace, evaluate, and test AI agents.

## Install

```bash
npm install @veval/sdk
```

Requires Node 18 or later. No runtime dependencies.

## Quick start

Wrap your agent with `runAsync` to send a trace to the Veval dashboard:

```javascript
import { VevalSdk } from "@veval/sdk";

const veval = new VevalSdk({ apiKey: "YOUR_API_KEY" });

const answer = await veval.runAsync("my-agent", async (ctx) => {
  return await ctx.trackStepAsync("call-llm", ctx.input, async (handle) => {
    const response = await callMyLlm(ctx.input);

    handle.setMeta("type", "llm");
    handle.setMeta("model", response.model);
    handle.setMeta("tokens_in", response.usage.input_tokens);
    handle.setMeta("tokens_out", response.usage.output_tokens);
    return response.text;
  });
}, "What is the capital of France?");
```

Every call to `trackStepAsync` records the step name, input, output, timing, and any metadata you attach.

Replay serves recorded outputs only to code inside a step. Check `ctx.isReplay` before work outside a step that must not run again, such as sending an email; it is true in every replay, mocked or live.

If a trace can't be stored (wrong API key, quota used up, network down), the SDK logs a `console.warn` with the HTTP status and carries on. Pass `throwOnSendFailure: true` in tests and CI to throw a `VevalSendError` instead.

## Testing

`VevalTestSdk` replays recorded traces through your agent code with the recorded outputs served for every step (matched by step name and identical input, reported in `result.fidelity`; a recorded step the agent never calls fails the replay), and `TraceAssert` provides assertions (`noErrors`, `maxSteps`, `judge`, `matchesSnapshot`, …) for replays and scenarios. A trace-backed scenario item replays live: steps recorded with `setMeta("type", "llm")` call the model, every other step returns its recorded output (`mock_model_steps: true` on the item, or `{ mock_model_steps: true }` as `runScenarioAsync`'s fifth argument for every item, for a fully mocked replay; `VevalTestSdk` always replays mocked; `live_model_steps: true` in `replayAsync` options for the same in a single replay). When a live replay leaves its recording (a step recorded as `tool` called with an input the recording doesn't have, or a step with no recording left), it stops at that step and is inconclusive: `outcome` is `"inconclusive"`, `passed` is false, assertions aren't evaluated, and `inconclusive` reports the step and both inputs. In a scenario, any inconclusive item fails the run unless `max_inconclusive_rate` in the scenario options allows a share of them; `summary` on replay, item and run results says it in one line. `step_rules` in the replay or scenario options (or on an item) matches chosen steps on name only (`{ match_on_name_only: true }`), ignores input fields (`ignore_input_fields`), or sets `on_divergence` per step. A model step the recording never contained runs live only if the call site marks it: `ctx.trackStepAsync(name, input, fn, { type: StepType.Llm })`. See the guides at [docs.veval.dev](https://docs.veval.dev).

To test failure paths, add `faults` to the replay options: replay serves a timeout, an HTTP error, a rate limit, a malformed output or your own error in place of a step's recorded output, so retries, fallbacks and guards run.

```javascript
const result = await veval.replayAsync(trace, (ctx) => agent.execute(ctx), {
  mock_llm_responses: true,
  faults: [ReplayFault.timeout("search_flights").times(1)], // fail once, then serve the recording
  assertions: [TraceAssert.maxAttempts("search_flights", 2)],
});
```

A fault that never fires fails the replay, and `result.faults` says how many times each fired. After a fault, a mocked replay serves only calls that still match their recording; any other call stops it as inconclusive.

`saveSnapshotAsync(name, traceIdOrCtx, options?)` stores a named baseline for `matchesSnapshot` and `compareSnapshotAsync`. The baseline and its trace are pinned, so retention doesn't delete them. Pins are limited per plan: at the limit, saving a new name throws a `VevalRequestError` with status 409. Pass `{ pinned: false }` to save a baseline that expires with retention.

## License

MIT
