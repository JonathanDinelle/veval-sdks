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

`VevalTestSdk` replays recorded traces through your agent code with the recorded outputs served for every step (matched by step name and identical input, reported in `result.fidelity`; a recorded step the agent never calls fails the replay), and `TraceAssert` provides assertions (`noErrors`, `maxSteps`, `judge`, `matchesSnapshot`, …) for replays and scenarios. A trace-backed scenario item replays live: steps recorded with `setMeta("type", "llm")` call the model, every other step returns its recorded output (`mock_model_steps: true` on the item, or `{ mock_model_steps: true }` as `runScenarioAsync`'s fifth argument for every item, for a fully mocked replay; `VevalTestSdk` always replays mocked; `live_model_steps: true` in `replayAsync` options for the same in a single replay). See the guides at [docs.veval.dev](https://docs.veval.dev).

## License

MIT
