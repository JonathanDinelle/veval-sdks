import { test } from "node:test";
import assert from "node:assert/strict";
import { ItemRunResult, ReplayFidelity, ReplayOptions, ReplayStoppedError, StepType, TraceAssert, TraceData, VevalExecutionContext } from "../src";
import { ScenarioReport } from "../src/scenario-report";
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

test("live replay stops inconclusive on an unrecorded tool call", async () => {
  let charged = false;
  const result = await sdk().replayAsync(briefingTrace(), async (ctx) => {
    await briefingAgent(PROMPT)(ctx);
    return ctx.trackStepAsync("charge_card", 42, async () => { charged = true; return "ok"; });
  }, { live_model_steps: true, assertions: [TraceAssert.outputContains("never")] });

  assert.equal(charged, false);
  assert.equal(result.outcome, "inconclusive");
  assert.equal(result.passed, false);
  assert.deepEqual(result.failures, [], "a stopped replay can't back up a verdict, so assertions are not evaluated");
  const stop = result.inconclusive!;
  assert.deepEqual([stop.step, stop.step_name, stop.reason], [3, "charge_card", "no_recording"]);
  assert.match(stop.message, /the trace never recorded it/);
  assert.match(stop.message, /replay mocked to test this change/);
});

test("live replay reports unused recordings without failing", async () => {
  const result = await sdk().replayAsync(briefingTrace(),
    (ctx) => ctx.trackStepAsync("write_briefing", PROMPT, async () => "OVERDUE: Dentist."),
    { live_model_steps: true, assertions: [] });

  assert.equal(result.passed, true, "a live model may legitimately take a different path");
  assert.deepEqual(result.fidelity!.unused.map((u) => u.step_name), ["visit_portal"]);
});

/**
 * A browser trace: the model fills a field, reads the page, and narrates what it does. Narration's text follows
 * the model's wording and doesn't change the page.
 */
const browserTrace = (): TraceData => ({
  trace_id: "tr_browser", agent_name: "browser", input: "book", output: null, status: "success", error: null,
  started_at: new Date().toISOString(), completed_at: null, duration_ms: null, total_cost_usd: null, metadata: {},
  steps: [
    recorded("plan", "book", "fill date 2026-11-02", "llm"),
    recorded("narrate", { text: "Filling the date" }, "ok", "custom"),
    recorded("fill", { field: "date", value: "2026-11-02" }, "filled", "tool"),
    recorded("page_snapshot", { reasoning: "check the form", tab: 1 }, "<form date=2026-11-02>", "tool"),
    recorded("answer", "<form date=2026-11-02>", "Booked for Nov 2.", "llm"),
  ],
});

/** The browser agent; each option is what the live model would now choose at that point. */
const browserAgent = (opts: { date?: string; narration?: string; reasoning?: string; toolsRun?: string[] } = {}) =>
  async (ctx: VevalExecutionContext) => {
    const { date = "2026-11-02", narration = "Filling the date", reasoning = "check the form", toolsRun } = opts;
    await ctx.trackStepAsync("plan", "book", async () => `fill date ${date}`);
    await ctx.trackStepAsync("narrate", { text: narration }, async () => { toolsRun?.push("narrate"); return "ok"; });
    await ctx.trackStepAsync("fill", { field: "date", value: date }, async () => { toolsRun?.push("fill"); return "filled"; });
    const page = await ctx.trackStepAsync("page_snapshot", { reasoning, tab: 1 }, async () => { toolsRun?.push("page_snapshot"); return "live page"; });
    return ctx.trackStepAsync("answer", page, async () => "Booked for Nov 2.");
  };

const liveReplay = (agent: (ctx: VevalExecutionContext) => Promise<unknown>, options: Partial<ReplayOptions> = {}) =>
  sdk().replayAsync(browserTrace(), agent, {
    ...options, live_model_steps: true, assertions: [TraceAssert.outputContains("Booked")],
  });

test("live replay stops where a diverged tool step leaves the recording", async () => {
  const toolsRun: string[] = [];
  const result = await liveReplay(browserAgent({ date: "2026-11-03", toolsRun }));

  assert.equal(result.outcome, "inconclusive");
  assert.deepEqual(result.failures, []);
  const stop = result.inconclusive!;
  assert.deepEqual([stop.step, stop.step_name, stop.reason], [3, "fill", "diverged"]);
  assert.ok(stop.message.includes('"value":"2026-11-02"'), stop.message);
  assert.ok(stop.message.includes('"value":"2026-11-03"'), stop.message);
  assert.match(stop.message, /re-record, or replay mocked to test this change/);
  assert.deepEqual(toolsRun, [], "no tool runs for real in a live replay, and nothing runs after the stop");
  const steps = result.replayed_context!.steps;
  assert.deepEqual(steps.map((s) => s.name), ["plan", "narrate", "fill"]);
  assert.equal(steps.at(-1)!.status, "error");
  assert.equal(result.status, "inconclusive", "the replay trace neither passed nor failed, so it mustn't raise the error alert");
  assert.equal(result.summary, "inconclusive at step 3 'fill' — 2/5 recorded steps used, 0 diverged but served, 1 live, left the path");
});

test("live replay passes when only a name-matched narration step changes", async () => {
  const result = await liveReplay(browserAgent({ narration: "Now I will fill in the date field" }), {
    stop_on_divergence_types: ["tool", "custom"],
    step_rules: { narrate: { match_on_name_only: true } },
  });

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.equal(result.fidelity!.diverged, 0);
});

test("live replay: ignored input fields don't count as divergence", async () => {
  const withoutRule = await liveReplay(browserAgent({ reasoning: "make sure the date stuck" }));
  const withRule = await liveReplay(browserAgent({ reasoning: "make sure the date stuck" }), {
    step_rules: { page_snapshot: { ignore_input_fields: ["reasoning"] } },
  });

  assert.equal(withoutRule.inconclusive!.step_name, "page_snapshot");
  assert.equal(withRule.outcome, "passed", withRule.failures.join("; "));
});

test("live replay ignores fields inside a JSON string input", async () => {
  // A model's tool-call arguments often arrive as a JSON string rather than an object.
  const trace = browserTrace();
  trace.steps[3].input = '{"reasoning":"check the form","tab":1}';
  const result = await sdk().replayAsync(trace, async (ctx) => {
    await ctx.trackStepAsync("plan", "book", async () => "fill");
    await ctx.trackStepAsync("narrate", { text: "Filling the date" }, async () => "ok");
    await ctx.trackStepAsync("fill", { field: "date", value: "2026-11-02" }, async () => "filled");
    const page = await ctx.trackStepAsync("page_snapshot", '{"tab": 1, "reasoning": "make sure the date stuck"}', async () => "live");
    return ctx.trackStepAsync("answer", page, async () => "Booked for Nov 2.");
  }, { live_model_steps: true, assertions: [], step_rules: { page_snapshot: { ignore_input_fields: ["reasoning"] } } });

  assert.equal(result.outcome, "passed", result.inconclusive?.message);
});

test("live replay serves a diverged untyped step by default and reports it", async () => {
  const result = await liveReplay(browserAgent({ narration: "Typing the date" }));

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  const diverged = result.fidelity!.bindings.filter((b) => b.kind === "diverged");
  assert.deepEqual(diverged.map((b) => b.step_name), ["narrate"]);
});

test("live replay: a step rule overrides the type default", async () => {
  const served = await liveReplay(browserAgent({ date: "2026-11-03" }), { step_rules: { fill: { on_divergence: "serve" } } });
  const stopped = await liveReplay(browserAgent({ narration: "Typing the date" }), { step_rules: { narrate: { on_divergence: "stop" } } });

  assert.equal(served.outcome, "passed", served.failures.join("; "));
  assert.equal(served.fidelity!.diverged, 1);
  assert.equal(stopped.inconclusive!.step_name, "narrate");
});

test("live replay stays inconclusive when the agent catches the stop", async () => {
  let laterStepThrew = false;
  const result = await liveReplay(async (ctx) => {
    try { await ctx.trackStepAsync("fill", { field: "date", value: "tomorrow" }, async () => "filled"); }
    catch { /* an agent that retries or swallows tool errors */ }
    try { await ctx.trackStepAsync("page_snapshot", { reasoning: "check the form", tab: 1 }, async () => "live"); }
    catch (err) { laterStepThrew = err instanceof ReplayStoppedError; }
    return "Booked anyway";
  });

  assert.equal(laterStepThrew, true, "every step after the stop throws it again");
  assert.equal(result.outcome, "inconclusive");
  assert.equal(result.inconclusive!.step_name, "fill");
  assert.deepEqual(result.failures, []);
});

test("mocked replay still serves a diverged tool step", async () => {
  const result = await sdk().replayAsync(browserTrace(), browserAgent({ date: "2026-11-03" }),
    { mock_llm_responses: true, assertions: [] });

  assert.equal(result.inconclusive, null, "stopping on divergence is for live replays; mocked replay serves every step");
  const diverged = result.fidelity!.bindings.filter((b) => b.kind === "diverged");
  assert.deepEqual(diverged.map((b) => b.step_name), ["fill"]);
});

test("scenario: an inconclusive item is counted apart, and an item rule replaces the scenario rule", async () => {
  const trace = browserTrace();
  const veval = sdk();
  veval.getTraceAsync = async () => trace;

  const result = await veval.runScenarioAsync("browser", browserAgent({ date: "2026-11-03" }), [],
    [
      { name: "stops", trace_id: trace.trace_id, assertions: [] },
      { name: "served", trace_id: trace.trace_id, assertions: [], step_rules: { fill: { on_divergence: "serve" } } },
    ],
    { step_rules: { fill: { on_divergence: "stop" } } });

  assert.equal(result.passed, false);
  assert.deepEqual([result.pass_count, result.fail_count, result.inconclusive_count], [1, 0, 1]);
  assert.equal(result.results[0].inconclusive!.step_name, "fill");
  assert.equal(result.results[0].passed, false);
  assert.equal(result.results[1].passed, true, result.results[1].failures.join("; "));
});

const passedItem = (): ItemRunResult => ({ item: { assertions: [] }, passed: true, failures: [] });
const replayedItem = (): ItemRunResult => ({ ...passedItem(), fidelity: new ReplayFidelity([]) });
const inconclusiveItem = (): ItemRunResult => ({
  item: { assertions: [] }, passed: false, failures: [],
  inconclusive: { step: 3, step_name: "fill", reason: "diverged", recorded_input: null, actual_input: null, message: "stopped" },
});

test("scenario run fails on any inconclusive item by default", () => {
  const result = ScenarioReport.result([replayedItem(), replayedItem(), replayedItem(), inconclusiveItem()]);

  assert.equal(result.passed, false, "a change that sends items off their recorded path mustn't look green");
  assert.equal(result.inconclusive_rate, 0.25);
  assert.equal(result.summary,
    "failed: too many inconclusive — 3 passed, 0 failed, 1 inconclusive (25% of replays > 0% allowed), 3/4 replays stayed on path");
});

test("scenario run passes when the inconclusive rate is within the threshold", () => {
  const result = ScenarioReport.result([replayedItem(), replayedItem(), replayedItem(), inconclusiveItem()], 0.25);

  assert.equal(result.passed, true);
  assert.equal(result.summary,
    "passed — 3 passed, 0 failed, 1 inconclusive (25% of replays, within 25% allowed), 3/4 replays stayed on path");
});

test("scenario run: synthetic items don't dilute the inconclusive rate", () => {
  // Only a replay can be inconclusive: two synthetic items mustn't turn one stop in two replays into 25%.
  const result = ScenarioReport.result([passedItem(), passedItem(), replayedItem(), inconclusiveItem()], 0.25);

  assert.equal(result.inconclusive_rate, 0.5);
  assert.equal(result.passed, false);
});

test("scenario run names failed items first when both reasons apply", () => {
  const failed: ItemRunResult = { item: { assertions: [] }, passed: false, failures: ["OutputContains failed"] };

  assert.ok(ScenarioReport.result([failed, inconclusiveItem()]).summary.startsWith("failed — 0 passed, 1 failed, 1 inconclusive"));
});

test("scenario run: a failed item fails the run whatever the threshold", () => {
  const failed: ItemRunResult = { item: { assertions: [] }, passed: false, failures: ["OutputContains failed"] };

  assert.equal(ScenarioReport.result([passedItem(), failed], 1).passed, false);
});

test("scenario run rejects a threshold outside 0 to 1", async () => {
  for (const rate of [-0.1, 1.5, NaN])
    await assert.rejects(sdk().runScenarioAsync("s", async () => "x", [], [], { max_inconclusive_rate: rate }), RangeError);
});

test("scenario run reports how many replays stayed on path", async () => {
  const onPath = await liveReplay(browserAgent());
  const offPath = await liveReplay(browserAgent({ date: "2026-11-03" }));
  const result = ScenarioReport.result([
    { ...passedItem(), fidelity: onPath.fidelity },
    { ...inconclusiveItem(), fidelity: offPath.fidelity, inconclusive: offPath.inconclusive },
    passedItem(),
  ]);

  assert.equal(result.stayed_on_path_rate, 0.5, "the synthetic item wasn't replayed, so it doesn't count");
  assert.ok(result.summary.endsWith(", 1/2 replays stayed on path"), result.summary);
  assert.equal(onPath.summary, "passed — 5/5 recorded steps used, 0 diverged but served, 2 live, stayed on path");
});

test("scenario run payload posts a third outcome and the stop", () => {
  const stop = { step: 3, step_name: "fill", reason: "diverged" as const, recorded_input: { value: "a" }, actual_input: { value: "b" }, message: "stopped" };
  const result = ScenarioReport.result([
    { ...passedItem(), item: { name: "ok", assertions: [] } },
    { item: { name: "bad", assertions: [] }, passed: false, failures: ["OutputContains failed"] },
    { item: { name: "off", assertions: [] }, passed: false, failures: [], inconclusive: stop },
  ]);

  const json = JSON.parse(JSON.stringify(ScenarioReport.runPayload(result, "synthetic")));

  assert.equal(json.fail_count, 1);
  assert.equal(json.inconclusive_count, 1);
  assert.equal(json.max_inconclusive_rate, 0);
  assert.ok(json.summary.startsWith("failed — 1 passed, 1 failed, 1 inconclusive"), json.summary);
  assert.deepEqual(json.results.map((i: { outcome: string }) => i.outcome), ["passed", "failed", "inconclusive"]);
  const off = json.results[2];
  assert.equal(off.passed, false);
  assert.deepEqual(off.failures, []);
  assert.equal(off.summary, "inconclusive at step 3 'fill'");
  assert.deepEqual(off.inconclusive, stop);
  assert.equal(json.results[0].inconclusive, null);
});

// VEV-37: the call site can say a step is a model call before it runs.

test("live replay runs a model step the recording never contained when the call site says llm", async () => {
  let extracted = false;
  const result = await sdk().replayAsync(briefingTrace(), async (ctx) => {
    const briefing = await briefingAgent(PROMPT)(ctx);
    await ctx.trackStepAsync("extraction", briefing, async () => { extracted = true; return "Dentist"; }, { type: StepType.Llm });
    return briefing;
  }, { live_model_steps: true, assertions: [TraceAssert.outputContains("OVERDUE")] });

  assert.equal(result.outcome, "passed", result.inconclusive?.message);
  assert.equal(extracted, true, "a model step runs live in a live replay");
  const last = result.fidelity!.bindings.at(-1)!;
  assert.deepEqual([last.step_name, last.kind, last.recorded_index], ["extraction", "live", null]);
  assert.equal(result.replayed_context!.steps.at(-1)!.type, "llm");
});

for (const type of [undefined, "tool"]) {
  test(`live replay still stops on an unrecorded step marked ${type ?? "nothing"}`, async () => {
    let ran = false;
    const result = await sdk().replayAsync(briefingTrace(), async (ctx) => {
      await briefingAgent(PROMPT)(ctx);
      return ctx.trackStepAsync("charge_card", 42, async () => { ran = true; return "ok"; }, { type });
    }, { live_model_steps: true, assertions: [] });

    assert.equal(ran, false);
    assert.deepEqual([result.inconclusive!.step_name, result.inconclusive!.reason], ["charge_card", "no_recording"]);
  });
}

test("live replay: the recorded type wins over the call site, and the conflict is reported", async () => {
  let portalVisits = 0;
  const result = await sdk().replayAsync(briefingTrace(), async (ctx) => {
    // A mislabelled tool call: the recording knows visit_portal is a tool, so it is still served, not run.
    await ctx.trackStepAsync("visit_portal", "today", async () => { portalVisits++; return "live"; }, { type: StepType.Llm });
    return ctx.trackStepAsync("write_briefing", PROMPT, async () => fakeModel(PROMPT));
  }, { live_model_steps: true, assertions: [] });

  assert.equal(portalVisits, 0);
  assert.equal(result.outcome, "passed", result.failures.join("; "));
  const [portal, briefing] = result.fidelity!.bindings;
  assert.deepEqual([portal.kind, portal.declared_type], ["matched", "llm"]);
  assert.equal(briefing.declared_type, null, "no type was declared at that call site");
});

test("mocked replay still refuses an unrecorded model step", async () => {
  let called = false;
  const result = await sdk().replayAsync(briefingTrace(), async (ctx) => {
    await briefingAgent(PROMPT)(ctx);
    return ctx.trackStepAsync("extraction", "x", async () => { called = true; return "y"; }, { type: StepType.Llm });
  }, { mock_llm_responses: true, assertions: [] });

  assert.equal(called, false, "mocked replay never makes a model call");
  assert.ok(result.failures.some((f) => f.includes("no mock output for step 'extraction'")), result.failures.join("; "));
});

test("a recorded run carries the call-site type, and setMeta still overrides it", async () => {
  const ctx = new VevalExecutionContext("tr_x", null);
  await ctx.trackStepAsync("extraction", "x", async () => "y", { type: StepType.Llm });
  await ctx.trackStepAsync("lookup", "x", async (h) => { h.setMeta("type", "tool"); return "y"; }, { type: StepType.Llm });
  await ctx.trackStepAsync("plain", "x", async () => "y");

  assert.deepEqual(ctx.steps.map((s) => s.type), ["llm", "tool", "custom"]);
});

test("live replay runs a step recorded untyped when the call site says llm", async () => {
  // An older trace, or a model call that threw before setMeta ran: the model step was saved as custom.
  const trace = briefingTrace();
  trace.steps[1].type = "custom";
  let modelCalls = 0;
  const result = await sdk().replayAsync(trace, async (ctx) => {
    await ctx.trackStepAsync("visit_portal", "today", async () => "live");
    return ctx.trackStepAsync("write_briefing", REGRESSED_PROMPT, async () => { modelCalls++; return fakeModel(REGRESSED_PROMPT); },
      { type: StepType.Llm });
  }, { live_model_steps: true, assertions: [TraceAssert.outputContains("OVERDUE")] });

  assert.equal(modelCalls, 1, "custom says nothing about the step, so the call site's llm applies");
  assert.equal(result.outcome, "failed", "the regressed prompt ran live and lost the overdue-first rule");
  const last = result.fidelity!.bindings.at(-1)!;
  assert.deepEqual([last.kind, last.declared_type], ["live", null]);
});
