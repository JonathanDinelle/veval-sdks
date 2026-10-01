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

## Testing

`VevalTestSdk` replays recorded traces through your agent code with the recorded outputs served for every step, and `TraceAssert` provides assertions (`noErrors`, `maxSteps`, `judge`, `matchesSnapshot`, …) for replays and scenarios. See the guides at [docs.veval.dev](https://docs.veval.dev).

## License

MIT
