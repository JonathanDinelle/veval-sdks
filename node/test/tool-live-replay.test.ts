import { test } from "node:test";
import assert from "node:assert/strict";
import { ReplayFault, ReplayOptions, StepType, TraceData, VevalExecutionContext } from "../src";
import { scenarioReplayOptions } from "../src/sdk";
import { recorded, sdk, testSdk } from "./helpers";

// Tool-live replay: tools run for real against their recorded inputs, the model is served from the recording.

/** A recorded run: the model picks a product, a pure tool formats its price, the model writes the answer. */
const trace = (): TraceData => ({
  trace_id: "tr_quote", agent_name: "quote", input: "how much is the lamp?", output: null, status: "success", error: null,
  started_at: new Date().toISOString(), completed_at: null, duration_ms: null, total_cost_usd: null, metadata: {},
  steps: [
    recorded("pick_product", "how much is the lamp?", "lamp", "llm"),
    recorded("format_price", "lamp", "EUR 12.50", "tool"),
    recorded("answer", "price: EUR 12.50", "The lamp costs EUR 12.50.", "llm"),
  ],
});

const toolLive: Partial<ReplayOptions> = { live_tool_steps: true, assertions: [] };

/** The agent under test; `format` is the tool's implementation. */
const agent = (format: (product: string) => string, onModel?: () => void) => async (ctx: VevalExecutionContext) => {
  const product = await ctx.trackStepAsync("pick_product", ctx.input, async () => { onModel?.(); return "live model"; },
    { type: StepType.Llm });
  const price = await ctx.trackStepAsync("format_price", product, async () => format(product), { type: StepType.Tool });
  return ctx.trackStepAsync("answer", `price: ${price}`, async () => { onModel?.(); return "live model"; }, { type: StepType.Llm });
};

test("an unchanged tool runs for real, the model comes from the recording, and the replay passes", async () => {
  let toolRuns = 0;
  let modelCalls = 0;
  const result = await sdk().replayAsync(trace(), agent(() => { toolRuns++; return "EUR 12.50"; }, () => modelCalls++), toolLive);

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.equal(toolRuns, 1);
  assert.equal(modelCalls, 0);
  assert.equal(result.output, "The lamp costs EUR 12.50.");
  assert.equal(result.fidelity!.tool_checks.length, 1);
  assert.equal(result.fidelity!.tool_checks[0].changed, false);
  assert.equal(result.replayed_context!.steps[1].metadata["_source"], "live_tool");
  assert.match(result.summary, /1 tools replayed, 0 not replayed, 0 outputs changed/);
});

test("a changed tool fails with both outputs, and the agent gets the new output", async () => {
  const result = await sdk().replayAsync(trace(), agent(() => "EUR 1250"), toolLive);

  assert.equal(result.outcome, "failed");
  assert.ok(result.failures.includes(
    `Tool output changed: 'format_price' (recorded step 2) ran with its recorded input and returned "EUR 1250" instead of "EUR 12.50".`));
  assert.equal(result.fidelity!.tool_output_changes[0].actual_output, "EUR 1250");
  assert.equal(result.replayed_context!.steps[2].input, "price: EUR 1250");
  assert.match(result.summary, /1 output changed/);
});

test("object outputs compare ignoring key order and ignored fields", async () => {
  const t = trace();
  t.steps[1].output = { amount: 12.5, currency: "EUR", request_id: "r1" };
  const result = await sdk().replayAsync(t, async (ctx) => {
    const product = await ctx.trackStepAsync("pick_product", ctx.input, async () => "x");
    await ctx.trackStepAsync("format_price", product, async () => ({ request_id: "r2", currency: "EUR", amount: 12.5 }),
      { type: StepType.Tool });
    return "done";
  }, { ...toolLive, step_rules: { format_price: { ignore_output_fields: ["request_id"] } } });

  assert.equal(result.fidelity!.tool_output_changes.length, 0);
});

test("a tool called with an unrecorded input is inconclusive", async () => {
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    await ctx.trackStepAsync("pick_product", ctx.input, async () => "x");
    return ctx.trackStepAsync("format_price", "LAMP", async () => "EUR 12.50", { type: StepType.Tool });
  }, toolLive);

  assert.equal(result.outcome, "inconclusive");
  assert.equal(result.inconclusive!.reason, "diverged");
  assert.ok(result.inconclusive!.message.startsWith("Tool-live replay stopped at step 2 ('format_price')"));
  assert.match(result.inconclusive!.message, /The model is served from the recording/);
});

test("a tool the recording never called is inconclusive", async () => {
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    await ctx.trackStepAsync("lookup_stock", "lamp", async () => 3, { type: StepType.Tool });
    return "";
  }, toolLive);

  assert.equal(result.outcome, "inconclusive");
  assert.equal(result.inconclusive!.reason, "no_recording");
});

test("a tool that reads external state is served, reported as not replayed, never as a pass", async () => {
  let ran = false;
  const result = await sdk().replayAsync(trace(), agent(() => { ran = true; return "live"; }),
    { ...toolLive, step_rules: { format_price: { reads_external_state: true } } });

  assert.equal(ran, false);
  assert.equal(result.fidelity!.not_replayed, 1);
  assert.equal(result.outcome, "failed");
  assert.equal(result.failures.length, 1);
  assert.ok(result.failures[0].startsWith("Tool-live replay of trace tr_quote ran no tool"));
  assert.match(result.failures[0], /except 1 that read external state/);
});

test("a tool that now throws is a change", async () => {
  const result = await sdk().replayAsync(trace(), agent(() => { throw new Error("bad currency"); }), toolLive);

  assert.ok(result.failures.includes(
    `Tool output changed: 'format_price' (recorded step 2) ran with its recorded input and threw (bad currency) where the recording returned "EUR 12.50".`));
});

test("a tool that failed in the recording and still fails is not a change", async () => {
  const t = trace();
  t.steps[1] = { ...t.steps[1], status: "error", error: "bad currency", output: null };
  const result = await sdk().replayAsync(t, async (ctx) => {
    const product = await ctx.trackStepAsync("pick_product", ctx.input, async () => "x");
    try {
      await ctx.trackStepAsync("format_price", product, async () => { throw new Error("bad currency"); }, { type: StepType.Tool });
    } catch {
      // fall back
    }
    return "fallback";
  }, toolLive);

  assert.equal(result.fidelity!.tool_checks[0].changed, false);
  assert.ok(!result.failures.some((f) => f.startsWith("Tool output changed")));
});

test("untyped steps are served from the recording", async () => {
  const t = trace();
  t.steps.splice(2, 0, recorded("narrate", "EUR 12.50", "narrated", "custom"));
  let narrated = false;
  const result = await sdk().replayAsync(t, async (ctx) => {
    const product = await ctx.trackStepAsync("pick_product", ctx.input, async () => "x");
    const price = await ctx.trackStepAsync("format_price", product, async () => "EUR 12.50", { type: StepType.Tool });
    await ctx.trackStepAsync("narrate", price, async () => { narrated = true; return "live"; });
    return ctx.trackStepAsync("answer", `price: ${price}`, async () => "live");
  }, toolLive);

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.equal(narrated, false);
});

test("live_tool_steps with live_model_steps throws", async () => {
  await assert.rejects(sdk().replayAsync(trace(), agent((p) => p), { ...toolLive, live_model_steps: true }), /can't be combined/);
});

test("faults work in a tool-live replay", async () => {
  let calls = 0;
  const format = async () => { calls++; return "EUR 12.50"; };
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    const product = await ctx.trackStepAsync("pick_product", ctx.input, async () => "x");
    let price: string;
    try { price = await ctx.trackStepAsync("format_price", product, format, { type: StepType.Tool }); }
    catch { price = await ctx.trackStepAsync("format_price", product, format, { type: StepType.Tool }); }
    return ctx.trackStepAsync("answer", `price: ${price}`, async () => "live");
  }, { ...toolLive, faults: [ReplayFault.timeout("format_price").times(1)] });

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.equal(calls, 1);
});

test("scenario: live_tool_steps runs tools in the test double", async () => {
  let toolRuns = 0;
  const run = await testSdk().withReplay(trace()).runScenarioAsync("quote", agent(() => { toolRuns++; return "EUR 1250"; }), [],
    [{ trace_id: "tr_quote", assertions: [] }], { live_tool_steps: true });

  assert.equal(toolRuns, 1);
  assert.equal(run.passed, false);
  assert.ok(run.results[0].failures.some((f) => f.startsWith("Tool output changed: 'format_price'")));
});

test("scenario: tool-live wins over mocked", () => {
  const options = scenarioReplayOptions({ assertions: [], mock_model_steps: true, live_tool_steps: true }, undefined, []);
  assert.equal(options.live_tool_steps, true);
  assert.equal(options.mock_llm_responses, false);
  assert.equal(options.live_model_steps, false);
});
