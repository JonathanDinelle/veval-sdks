import { test } from "node:test";
import assert from "node:assert/strict";
import { ReplayOptions, TraceData, VevalExecutionContext } from "../src";
import { recorded, sdk, testSdk } from "./helpers";

// ctx.isReplay. Mirrors sdk/python/tests/test_sync_and_is_replay.py.

const bookingTrace = (): TraceData => ({
  trace_id: "tr_booking", agent_name: "booking", input: "Friday", output: null, status: "success", error: null,
  started_at: new Date().toISOString(), completed_at: null, duration_ms: null, total_cost_usd: null, metadata: {},
  steps: [
    recorded("find_slot", "Friday", "10:00", "tool"),
    recorded("write_reply", "10:00", "Booked for 10:00.", "llm"),
  ],
});

const isReplayOf = (seen: boolean[]) => async (ctx: VevalExecutionContext) => {
  seen.push(ctx.isReplay);
  const slot = await ctx.trackStepAsync("find_slot", ctx.input, async () => "11:30");
  return ctx.trackStepAsync("write_reply", slot, async () => `Booked for ${slot}.`);
};

test("isReplay is false in a normal run", async () => {
  const seen: boolean[] = [];
  await sdk().runAsync("booking", isReplayOf(seen), "Friday");
  assert.deepEqual(seen, [false]);
});

for (const [mode, options] of [
  ["unmocked", {}],
  ["mocked", { mock_llm_responses: true }],
  ["live", { live_model_steps: true }],
] as Array<[string, Partial<ReplayOptions>]>) {
  test(`isReplay is true in a ${mode} replay`, async () => {
    const seen: boolean[] = [];
    await sdk().replayAsync(bookingTrace(), isReplayOf(seen), options);
    assert.deepEqual(seen, [true]);
  });
}

test("isReplay is true under the test SDK withReplay", async () => {
  const seen: boolean[] = [];
  await testSdk().withReplay(bookingTrace()).runAsync("booking", isReplayOf(seen), "Friday");
  assert.deepEqual(seen, [true]);
});

test("isReplay lets a live replay skip a side effect outside any step", async () => {
  const sentEmails: unknown[] = [];
  const agent = async (ctx: VevalExecutionContext) => {
    const reply = await ctx.trackStepAsync("write_reply", "10:00", async () => "Booked for 10:00.");
    if (!ctx.isReplay) sentEmails.push(reply);
    return reply;
  };
  const trace: TraceData = { ...bookingTrace(), trace_id: "tr_reply", steps: [recorded("write_reply", "10:00", "Booked for 10:00.", "llm")] };

  await sdk().runAsync("booking", agent, "Friday");
  const result = await sdk().replayAsync(trace, agent, { live_model_steps: true });

  assert.ok(result.passed, result.failures.join("; "));
  assert.deepEqual(sentEmails, ["Booked for 10:00."]);
});

test("isReplay marks trace-backed scenario items but not synthetic ones", async () => {
  const seen: boolean[] = [];
  await testSdk().withReplay(bookingTrace()).runScenarioAsync("booking", isReplayOf(seen), [], [
    { name: "recorded", trace_id: "tr_booking", assertions: [] },
    { name: "synthetic", input: "Monday", assertions: [] },
  ]);
  assert.deepEqual(seen, [true, false]);
});

test("user code can't mark a run as a replay", async () => {
  const pkg: Record<string, unknown> = await import("../src");
  assert.equal("markAsReplay" in pkg, false);
  assert.equal("markAsReplay" in new VevalExecutionContext("tr_x", null), false);
});
