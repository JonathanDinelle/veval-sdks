import { test } from "node:test";
import assert from "node:assert/strict";
import { TraceAssert, TraceData, VevalExecutionContext } from "../src";
import { recorded, sdk, testSdk } from "./helpers";

// Live replay: model steps run for real, every other step is served from the recording.

const PROMPT = "List overdue items first.";
const REGRESSED_PROMPT = "List items alphabetically.";

/** A production trace: a portal visit (tool) and a model call that wrote the briefing. */
const briefingTrace = (): TraceData => ({
  trace_id: "tr_briefing", agent_name: "briefing", input: "today", output: null, status: "success", error: null,
  started_at: new Date().toISOString(), completed_at: null, duration_ms: null, total_cost_usd: null, metadata: {},
  steps: [
    recorded("visit_portal", "today", "Dentist (overdue); Taxes", "tool"),
    recorded("write_briefing", PROMPT, "OVERDUE: Dentist. Then: Taxes.", "llm"),
  ],
});

/** Stands in for the model: it only puts overdue items first when the prompt asks for it. */
const fakeModel = (prompt: string) => (prompt.includes("overdue") ? "OVERDUE: Dentist. Then: Taxes." : "Dentist. Taxes.");

const briefingAgent = (prompt: string, onPortalVisit?: () => void) => async (ctx: VevalExecutionContext) => {
  await ctx.trackStepAsync("visit_portal", "today", async () => { onPortalVisit?.(); return "live portal data"; });
  return ctx.trackStepAsync("write_briefing", prompt, async () => fakeModel(prompt));
};

/** Runs a trace-backed scenario item through VevalSdk, with the trace served locally instead of from the API. */
const runScenario = (prompt: string, opts: { itemMock?: boolean; scenarioMock?: boolean; onPortalVisit?: () => void } = {}) => {
  const trace = briefingTrace();
  const veval = sdk();
  veval.getTraceAsync = async () => trace;
  return veval.runScenarioAsync("briefing", briefingAgent(prompt, opts.onPortalVisit),
    [TraceAssert.outputContains("OVERDUE")],
    [{ trace_id: trace.trace_id, mock_model_steps: opts.itemMock, assertions: [] }],
    { mock_model_steps: opts.scenarioMock });
};

test("trace-backed scenario catches a regressed prompt and passes the unchanged one", async () => {
  let portalVisits = 0;
  const unchanged = await runScenario(PROMPT, { onPortalVisit: () => portalVisits++ });
  const regressed = await runScenario(REGRESSED_PROMPT, { onPortalVisit: () => portalVisits++ });

  assert.equal(unchanged.passed, true, unchanged.results[0].failures.join("; "));
  assert.equal(regressed.passed, false);
  assert.ok(regressed.results[0].failures.some((f) => /OutputContains/.test(f)));
  assert.equal(portalVisits, 0, "tool steps are served from the recording");
});

test("trace-backed scenario mocked by the item or the whole scenario cannot see the prompt change", async () => {
  assert.equal((await runScenario(REGRESSED_PROMPT, { itemMock: true })).passed, true);
  assert.equal((await runScenario(REGRESSED_PROMPT, { scenarioMock: true })).passed, true);
});

test("test SDK scenario always replays mocked", async () => {
  const trace = briefingTrace();
  let modelCalls = 0;
  const result = await testSdk().withReplay(trace).runScenarioAsync("briefing", async (ctx) => {
    await ctx.trackStepAsync("visit_portal", "today", async () => "live");
    return ctx.trackStepAsync("write_briefing", REGRESSED_PROMPT, async () => { modelCalls++; return "live"; });
  }, [TraceAssert.outputContains("OVERDUE")], [{ trace_id: trace.trace_id, assertions: [] }]);

  assert.equal(result.passed, true, "the test double serves the recorded model output");
  assert.equal(modelCalls, 0, "the test double never makes a live model call");
});

test("live replay reports model steps as live", async () => {
  const result = await sdk().replayAsync(briefingTrace(), briefingAgent(PROMPT), { live_model_steps: true, assertions: [] });

  assert.equal(result.passed, true);
  assert.equal(result.fidelity!.live, 1);
  assert.deepEqual(result.fidelity!.bindings.map((b) => [b.step_name, b.kind]), [["visit_portal", "matched"], ["write_briefing", "live"]]);
});

test("live replay serves untyped steps from the recording", async () => {
  const trace = briefingTrace();
  trace.steps.unshift(recorded("load_profile", "u_1", "profile", "custom"));
  let ran = false;

  const result = await sdk().replayAsync(trace, async (ctx) => {
    const profile = await ctx.trackStepAsync("load_profile", "u_1", async () => { ran = true; return "live"; });
    assert.equal(profile, "profile");
    return briefingAgent(PROMPT)(ctx);
  }, { live_model_steps: true, assertions: [] });

  assert.equal(result.passed, true);
  assert.equal(ran, false);
});

test("live replay fails when no step ran live", async () => {
  const trace = briefingTrace();
  trace.steps[1].type = "custom"; // the model step was never marked as llm

  const result = await sdk().replayAsync(trace, briefingAgent(REGRESSED_PROMPT), { live_model_steps: true, assertions: [] });

  assert.equal(result.passed, false);
  assert.equal(result.failures.length, 1);
  assert.match(result.failures[0], /ran no step live/);
  assert.match(result.failures[0], /setMeta\("type", "llm"\)/);
});

test("live replay runs an extra call of a model step live", async () => {
  const result = await sdk().replayAsync(briefingTrace(), async (ctx) => {
    await briefingAgent(PROMPT)(ctx);
    return ctx.trackStepAsync("write_briefing", "Shorten it.", async () => "OVERDUE: Dentist.");
  }, { live_model_steps: true, assertions: [] });

  assert.equal(result.passed, true);
  assert.equal(result.output, "OVERDUE: Dentist.");
  const last = result.fidelity!.bindings.at(-1)!;
  assert.deepEqual([last.kind, last.recorded_index], ["live", null]);
});

test("live replay refuses to run an unrecorded tool call", async () => {
  let charged = false;
  const result = await sdk().replayAsync(briefingTrace(), async (ctx) => {
    await briefingAgent(PROMPT)(ctx);
    return ctx.trackStepAsync("charge_card", 42, async () => { charged = true; return "ok"; });
  }, { live_model_steps: true, assertions: [] });

  assert.equal(result.passed, false);
  assert.equal(result.failures.length, 1);
  assert.match(result.failures[0], /Live replay: step 'charge_card' has no recording/);
  assert.equal(charged, false);
});

test("live replay reports unused recordings without failing", async () => {
  const result = await sdk().replayAsync(briefingTrace(),
    (ctx) => ctx.trackStepAsync("write_briefing", PROMPT, async () => "OVERDUE: Dentist."),
    { live_model_steps: true, assertions: [] });

  assert.equal(result.passed, true, "a live model may legitimately take a different path");
  assert.deepEqual(result.fidelity!.unused.map((u) => u.step_name), ["visit_portal"]);
});
