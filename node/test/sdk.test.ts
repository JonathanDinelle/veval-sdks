import { test } from "node:test";
import assert from "node:assert/strict";
import { resolveOptions, TraceAssert, TraceData, VevalExecutionContext, VevalSdk } from "../src";
import { recorded, run, sdk, testSdk } from "./helpers";

const trace = (...steps: ReturnType<typeof recorded>[]): TraceData => ({
  trace_id: "tr_recorded", agent_name: "agent", input: "hello", output: null, status: "success", error: null,
  started_at: new Date().toISOString(), completed_at: null, duration_ms: null, total_cost_usd: null,
  metadata: {}, steps,
});

// --- options ---------------------------------------------------------------------------------------

test("endpoint comes from VEVAL_INTERNAL_ENDPOINT, not a public option", () => {
  assert.equal(resolveOptions({ apiKey: "k" }).endpoint, "http://127.0.0.1:1");
  assert.equal("endpoint" in ({ apiKey: "k" } as Record<string, unknown>), false);
});

test("trace payload does not send the deprecated project_id", async () => {
  const ctx = await run(["step", "in", "out"]);
  const payload = VevalSdk.buildPayload("tr", "agent", ctx, null, null, "success", null, new Date(), new Date());
  assert.equal("project_id" in payload, false);
});

// --- running and steps -----------------------------------------------------------------------------

test("runAsync returns the result and records steps with metadata", async () => {
  let seen: VevalExecutionContext | null = null;
  const result = await sdk().runAsync("agent", async (ctx) => {
    seen = ctx;
    ctx.setMetadata("user_id", "u_1");
    return ctx.trackStepAsync("call-llm", "prompt", async (step) => {
      step.setMeta("type", "llm");
      step.setMeta("cost_usd", 0.01);
      step.setMeta("provider", "anthropic");
      return "answer";
    });
  });

  assert.equal(result, "answer");
  const step = seen!.steps[0];
  assert.deepEqual([step.name, step.type, step.costUsd, step.output, step.status], ["call-llm", "llm", 0.01, "answer", "success"]);
  assert.equal(step.metadata["provider"], "anthropic");
  assert.equal(seen!.traceMeta["user_id"], "u_1");
});

test("runAsync rethrows agent errors and marks the step failed", async () => {
  let seen: VevalExecutionContext | null = null;
  await assert.rejects(
    sdk().runAsync("agent", async (ctx) => {
      seen = ctx;
      return ctx.trackStepAsync("boom", null, async () => { throw new Error("kaboom"); });
    }),
    /kaboom/
  );
  assert.deepEqual([seen!.steps[0].status, seen!.steps[0].error], ["error", "kaboom"]);
});

// --- replay ----------------------------------------------------------------------------------------

test("replay serves recorded outputs and never runs the step", async () => {
  let ran = false;
  const result = await sdk().replayAsync(trace(recorded("classify", "hello", "greeting")),
    (ctx) => ctx.trackStepAsync("classify", "hello", async () => { ran = true; return "live"; }),
    { mock_llm_responses: true, assertions: [] });

  assert.equal(result.output, "greeting");
  assert.equal(ran, false);
  assert.equal(result.passed, true);
});

test("replay fails when a step has no recording, instead of calling live", async () => {
  const result = await sdk().replayAsync(trace(recorded("classify", "hello", "greeting")),
    (ctx) => ctx.trackStepAsync("unrecorded", null, async () => "live"),
    { mock_llm_responses: true, assertions: [] });

  assert.equal(result.passed, false);
  assert.match(result.failures[0], /no mock output for step 'unrecorded'/);
});

test("withReplay makes the production entry point replay unchanged", async () => {
  const replaying = testSdk().withReplay(trace(recorded("classify", "hello", "greeting")));
  const output = await replaying.runAsync("agent", (ctx) => ctx.trackStepAsync("classify", "hello", async () => "live"));

  assert.equal(output, "greeting");
  assert.equal(replaying.lastStatus, "success");
});

// --- assertions ------------------------------------------------------------------------------------

test("built-in assertions pass and fail as documented", async () => {
  const ctx = new VevalExecutionContext("tr", null);
  await ctx.trackStepAsync("fetch", "q", async (step) => { step.setMeta("type", "tool"); step.setMeta("cost_usd", 0.2); return "Montreal weather"; });
  await ctx.trackStepAsync("write", "d", async () => "briefing");

  const check = async (a: ReturnType<typeof TraceAssert.noErrors>) => a.evaluate(ctx);
  assert.equal(await check(TraceAssert.noErrors()), null);
  assert.equal(await check(TraceAssert.maxSteps(2)), null);
  assert.match((await check(TraceAssert.maxSteps(1)))!, /^MaxSteps/);
  assert.equal(await check(TraceAssert.stepExists("write")), null);
  assert.match((await check(TraceAssert.stepExists("nope")))!, /^StepExists/);
  assert.equal(await check(TraceAssert.toolCalled("fetch")), null);
  assert.match((await check(TraceAssert.toolCalled("write")))!, /^ToolCalled/, "write is not a tool step");
  assert.match((await check(TraceAssert.maxCost(0.1)))!, /^MaxCost/);
  assert.equal(await check(TraceAssert.outputContains("Montreal")), null);
  assert.match((await check(TraceAssert.outputContains("Paris")))!, /^OutputContains/);
});

// --- judge -----------------------------------------------------------------------------------------

test("judge mock passes, records the verdict, and reports failures with score and reasoning", async () => {
  const judged = testSdk()
    .withJudgeMock("polite", true, 0.9, "Friendly.")
    .withJudgeMock("concise", false, 0.4, "Rambles.");
  const ctx = await run(["reply", "hi", "Hello there!"]);

  assert.equal(await TraceAssert.judge(judged, "polite").evaluate(ctx), null);
  const failure = await TraceAssert.judge(judged, "concise").evaluate(ctx);
  assert.equal(failure, "Judge: concise (score 0.40) — Rambles.");
  assert.deepEqual(ctx.judgments.map((j) => [j.criteria, j.passed]), [["polite", true], ["concise", false]]);
});

test("judge without a mock fails instead of making a billed call", async () => {
  const failure = await TraceAssert.judge(testSdk(), "unmocked").evaluate(await run(["reply", "hi", "yo"]));
  assert.match(failure!, /^Judge: evaluation failed — .*no judge mock for criteria 'unmocked'/);
});

// --- scenarios -------------------------------------------------------------------------------------

test("scenario runs each item with shared and per-item assertions", async () => {
  const agent = (ctx: VevalExecutionContext) =>
    ctx.trackStepAsync("reply", ctx.input, async () => {
      if (ctx.input === "crash") throw new Error("agent crashed");
      return `echo: ${ctx.input}`;
    });

  const result = await testSdk().runScenarioAsync("echo", agent, [TraceAssert.stepExists("reply")], [
    { name: "ok", input: "hi", assertions: [TraceAssert.outputContains("echo: hi")] },
    { name: "wrong output", input: "bye", assertions: [TraceAssert.outputContains("echo: hi")] },
    { name: "crash", input: "crash", assertions: [] },
  ]);

  assert.deepEqual(result.results.map((r) => r.passed), [true, false, false]);
  assert.equal(result.pass_count, 1);
  assert.ok(result.results[2].failures.some((f) => f.includes("agent crashed")));
});
