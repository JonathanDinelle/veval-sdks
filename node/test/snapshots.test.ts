import { test } from "node:test";
import assert from "node:assert/strict";
import { SnapshotComparer, SnapshotData, SnapshotDataHelper, TraceAssert, VevalExecutionContext } from "../src";
import { recorded, run, sdk, testSdk } from "./helpers";

const snap = async (...steps: Array<[string, unknown, unknown]>) => SnapshotDataHelper.fromContext(await run(...steps));

test("identical run has no changes", async () => {
  const diff = SnapshotComparer.compare(await snap(["classify", "q", "a"], ["respond", "a", "b"]), await run(["classify", "q", "a"], ["respond", "a", "b"]));
  assert.equal(diff.has_changes, false);
  assert.match(diff.summary(), /no changes/);
});

test("an extra call of a known step is reported as added", async () => {
  const diff = SnapshotComparer.compare(await snap(["fetch", "x", "n"], ["write", "n", "b"]),
    await run(["fetch", "x", "n"], ["write", "n", "b"], ["write", "n", "b"]));
  assert.deepEqual(diff.changes.map((c) => [c.kind, c.step_name, c.occurrence]), [["Added", "write", 2]]);
  assert.deepEqual(diff.added_steps, ["write"]);
});

test("a changed prompt is reported as an input change with a line diff", async () => {
  const diff = SnapshotComparer.compare(await snap(["write", "System: overdue first.\nData", "b"]),
    await run(["write", "System: alphabetical.\nData", "b"]));
  assert.equal(diff.changes[0].kind, "InputChanged");
  assert.ok(diff.changes[0].detail!.includes("- System: overdue first."));
  assert.ok(diff.changes[0].detail!.includes("+ System: alphabetical."));
});

test("a prompt inside an object diffs line by line", async () => {
  const diff = SnapshotComparer.compare(await snap(["write", { model: "m", system: "You are helpful.\n1. Overdue first.\n2. Today." }, "b"]),
    await run(["write", { model: "m", system: "You are helpful.\n1. Alphabetical.\n2. Today." }, "b"]));
  const detail = diff.changes[0].detail!;
  const lines = detail.split("\n");
  assert.ok(lines.some((l) => l.startsWith("-") && l.replace(/^[-\s]+/, "") === "1. Overdue first."), detail);
  assert.ok(lines.some((l) => l.startsWith("+") && l.replace(/^[+\s]+/, "") === "1. Alphabetical."), detail);
  assert.ok(!detail.includes("\\n") && lines.length < 6, detail);
});

test("API JSON compares equal to live objects regardless of key order", async () => {
  const baseline = SnapshotDataHelper.fromTrace({ trace_id: "tr", steps: [recorded("search", JSON.parse('{"limit":5,"query":"w"}'), "r")] } as never);
  assert.equal(SnapshotComparer.compare(baseline, await run(["search", { query: "w", limit: 5 }, "r"])).has_changes, false);
});

test("ignoreFields drops volatile values", async () => {
  const baseline = await snap(["call", { prompt: "p", timestamp: "1" }, "r"]);
  const current = await run(["call", { prompt: "p", timestamp: "2" }, "r"]);
  assert.equal(SnapshotComparer.compare(baseline, current).has_changes, true);
  assert.equal(SnapshotComparer.compare(baseline, current, { ignoreFields: ["timestamp"] }).has_changes, false);
});

test("outputs are compared only when enabled", async () => {
  const baseline = await snap(["tool", "in", "sunny"]);
  const current = await run(["tool", "in", "snow"]);
  assert.equal(SnapshotComparer.compare(baseline, current).has_changes, false);
  assert.deepEqual(SnapshotComparer.compare(baseline, current, { compareOutputs: true }).changes.map((c) => c.kind), ["OutputChanged"]);
});

test("a reordered step is reported once, as moved", async () => {
  const diff = SnapshotComparer.compare(await snap(["a", 1, 1], ["b", 1, 1], ["c", 1, 1]), await run(["b", 1, 1], ["c", 1, 1], ["a", 1, 1]));
  assert.deepEqual(diff.changes.map((c) => c.kind), ["Moved"]);
  assert.equal(diff.order_changes.length, 1);
});

test("a legacy names-only snapshot compares the sequence only, and says so", async () => {
  const legacy: SnapshotData = { steps: [], step_names: ["write"], step_order: ["write"], step_count: 1 };
  const diff = SnapshotComparer.compare(legacy, await run(["write", "x", "b"], ["write", "y", "b"]));
  assert.equal(diff.compared_content, false);
  assert.match(diff.summary(), /legacy snapshot/);
});

test("compare_with_recording catches a recorded output bound to the wrong call", async () => {
  const recording = { trace_id: "tr_rec", input: null, steps: [recorded("call-claude", "Classify", "category: urgent"), recorded("call-claude", "Answer", "briefing")] };
  const result = await sdk().replayAsync(recording as never,
    (ctx) => ctx.trackStepAsync("call-claude", "Answer", async () => "live"),
    { mock_llm_responses: true, assertions: [], compare_with_recording: {} });

  assert.equal(result.output, "category: urgent", "replay binds recorded outputs by step name");
  assert.equal(result.passed, false);
  const kinds = result.recording_diff!.changes.map((c) => c.kind);
  assert.ok(kinds.includes("InputChanged") && kinds.includes("Removed"));
});

test("replayed steps keep their recorded type, so toolCalled passes", async () => {
  const recording = { trace_id: "t", input: null, steps: [recorded("fetch-weather", "d", "sunny", "tool")] };
  const result = await sdk().replayAsync(recording as never,
    (ctx) => ctx.trackStepAsync("fetch-weather", "d", async () => "live"),
    { mock_llm_responses: true, assertions: [TraceAssert.toolCalled("fetch-weather")], compare_with_recording: {} });
  assert.deepEqual(result.failures, []);
});

test("matchesSnapshot: by value, by name, missing, and unreachable", async () => {
  const baseline = await snap(["write", "overdue first", "b"]);
  const failure = await TraceAssert.matchesSnapshot(baseline).evaluate(await run(["write", "alphabetical", "b"]));
  assert.ok(failure!.startsWith("MatchesSnapshot:") && failure!.includes("+ alphabetical"));

  const withBaseline = testSdk().withSnapshot("briefing", baseline);
  assert.equal(await TraceAssert.matchesSnapshot(withBaseline, "briefing").evaluate(await run(["write", "overdue first", "b"])), null);

  const unreachable = await TraceAssert.matchesSnapshot(withBaseline, "not-local").evaluate(new VevalExecutionContext("t", null));
  assert.match(unreachable!, /could not load snapshot 'not-local'/);

  const missing = await TraceAssert.matchesSnapshot({ getSnapshotAsync: async () => null }, "nope").evaluate(new VevalExecutionContext("t", null));
  assert.match(missing!, /no snapshot named 'nope'/);
});
