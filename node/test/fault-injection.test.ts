import { test } from "node:test";
import assert from "node:assert/strict";
import { FaultHttpError, FaultTimeoutError, ReplayFault, ReplayOptions, TraceAssert, TraceData, VevalExecutionContext } from "../src";
import { ScenarioReport } from "../src/scenario-report";
import { recorded, sdk, testSdk } from "./helpers";

// Fault injection: replay serves a declared failure instead of a step's recorded output.

const FLIGHTS = '["AF123","BA456"]';

/** A recorded run: one flight search, then a model call that summarizes it. */
const trace = (): TraceData => ({
  trace_id: "tr_faults", agent_name: "travel", input: "Paris", output: "Found AF123", status: "success", error: null,
  started_at: new Date().toISOString(), completed_at: null, duration_ms: null, total_cost_usd: null, metadata: {},
  steps: [
    recorded("search_flights", "Paris", FLIGHTS, "tool"),
    recorded("summarize", `flights: ${FLIGHTS}`, "Found AF123", "llm"),
  ],
});

/** The agent under test: retries the search up to `retries` times, then summarizes. */
const agent = (retries: number) => async (ctx: VevalExecutionContext) => {
  let flights: string;
  for (let attempt = 0; ; attempt++) {
    try {
      flights = await ctx.trackStepAsync("search_flights", "Paris", async () => "live");
      break;
    } catch (err) {
      if (attempt >= retries) throw err;
    }
  }
  return ctx.trackStepAsync("summarize", `flights: ${flights}`, async () => "live");
};

const mocked = (...faults: ReplayFault[]): Partial<ReplayOptions> => ({ mock_llm_responses: true, assertions: [], faults });

/** Runs `fn` as the agent and returns what the first step call threw. */
async function caughtFrom(fault: ReplayFault): Promise<unknown> {
  let caught: unknown;
  await sdk().replayAsync(trace(), async (ctx) => {
    try { await ctx.trackStepAsync("search_flights", "Paris", async () => "live"); } catch (err) { caught = err; }
    return "";
  }, mocked(fault));
  return caught;
}

test("timeout on the first call, retry recovers: passes", async () => {
  const result = await sdk().replayAsync(trace(), agent(2), {
    ...mocked(ReplayFault.timeout("search_flights").times(1)),
    assertions: [TraceAssert.maxAttempts("search_flights", 1), TraceAssert.noErrors()],
  });

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.equal(result.output, "Found AF123");
  assert.equal(result.faults.length, 1);
  assert.equal(result.faults[0].fired, 1);
  assert.equal(result.fidelity!.faulted, 1);
  assert.equal(result.fidelity!.unused.length, 0);
  const faulted = result.replayed_context!.steps[0];
  assert.equal(faulted.status, "error");
  assert.equal(faulted.metadata["_source"], "fault");
  assert.equal(faulted.type, "tool");
  assert.match(result.summary, /1 fault injected/);
});

test("timeout without a retry policy: fails", async () => {
  const result = await sdk().replayAsync(trace(), agent(0), mocked(ReplayFault.timeout("search_flights").times(1)));

  assert.equal(result.outcome, "failed");
  assert.ok(result.failures.some((f) => f.includes("Replay threw exception") && f.includes("Injected fault")));
});

test("timeout throws an error named TimeoutError", async () => {
  const caught = await caughtFrom(ReplayFault.timeout("search_flights"));
  assert.ok(caught instanceof FaultTimeoutError);
  assert.equal((caught as Error).name, "TimeoutError");
});

test("a limited fault the agent swallows without retrying fails on the unused recording", async () => {
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    try { return await ctx.trackStepAsync("search_flights", "Paris", async () => "live"); } catch { return "Sorry, try later"; }
  }, mocked(ReplayFault.timeout("search_flights").times(1)));

  assert.equal(result.outcome, "failed");
  assert.ok(result.failures.some((f) => f.includes("never called")));
});

test("an every-call fault whose fallback ends the run: passes, unused recordings still reported", async () => {
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    try {
      return await ctx.trackStepAsync("search_flights", "Paris", async () => "live");
    } catch (err) {
      if (err instanceof FaultHttpError && err.status === 503) return "Search is down, try later";
      throw err;
    }
  }, { ...mocked(ReplayFault.httpError("search_flights", 503)), assertions: [TraceAssert.noErrors()] });

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.equal(result.output, "Search is down, try later");
  assert.equal(result.fidelity!.unused.length, 2);
});

test("a fallback tool the recording never called is inconclusive in mocked mode", async () => {
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    try {
      return await ctx.trackStepAsync("search_flights", "Paris", async () => "live");
    } catch {
      return ctx.trackStepAsync("search_trains", "Paris", async () => "live", { type: "tool" });
    }
  }, mocked(ReplayFault.httpError("search_flights", 500)));

  assert.equal(result.outcome, "inconclusive");
  assert.equal(result.passed, false);
  assert.equal(result.inconclusive!.step_name, "search_trains");
  assert.equal(result.inconclusive!.reason, "after_fault");
  assert.ok(result.inconclusive!.message.startsWith(
    "Replay stopped at step 2 ('search_trains') after an injected fault (HTTP 500 on 'search_flights')"));
  assert.equal(result.status, "inconclusive");
});

test("after a fault, a model call whose input carries the error is inconclusive in mocked mode", async () => {
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    let flights: string;
    try { flights = await ctx.trackStepAsync("search_flights", "Paris", async () => "live"); }
    catch (err) { flights = `error: ${(err as Error).message}`; }
    return ctx.trackStepAsync("summarize", `flights: ${flights}`, async () => "live");
  }, mocked(ReplayFault.timeout("search_flights")));

  assert.equal(result.outcome, "inconclusive");
  assert.equal(result.inconclusive!.step_name, "summarize");
  assert.equal(result.inconclusive!.reason, "after_fault");
  assert.match(result.inconclusive!.message, /its input matches no unused recording of 'summarize'/);
});

test("live replay: the model step after a fault runs live", async () => {
  let modelSaw: string | null = null;
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    let flights: string;
    try { flights = await ctx.trackStepAsync("search_flights", "Paris", async () => "live"); } catch { flights = "unavailable"; }
    return ctx.trackStepAsync("summarize", `flights: ${flights}`, async () => {
      modelSaw = flights;
      return "No flights right now";
    });
  }, { live_model_steps: true, assertions: [], faults: [ReplayFault.timeout("search_flights")] });

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.equal(modelSaw, "unavailable");
  assert.equal(result.output, "No flights right now");
});

test("rate limit carries status and Retry-After", async () => {
  const caught = await caughtFrom(ReplayFault.rateLimit("search_flights", 2));
  assert.ok(caught instanceof FaultHttpError);
  assert.equal(caught.status, 429);
  assert.equal(caught.retry_after_seconds, 2);
  assert.equal(caught.headers["retry-after"], "2");
});

test("a custom error is thrown as built", async () => {
  class VendorError extends Error {}
  const caught = await caughtFrom(ReplayFault.throw("search_flights", () => new VendorError("vendor down")));
  assert.ok(caught instanceof VendorError);
  assert.equal(caught.message, "vendor down");
});

test("truncated output is half the recorded JSON, then the guard retries", async () => {
  const outputs: string[] = [];
  const result = await sdk().replayAsync(trace(), async (ctx) => {
    for (;;) {
      const raw = await ctx.trackStepAsync("search_flights", "Paris", async () => "live");
      outputs.push(raw);
      try { JSON.parse(raw); } catch { continue; }
      return ctx.trackStepAsync("summarize", `flights: ${raw}`, async () => "live");
    }
  }, {
    ...mocked(ReplayFault.malformed("search_flights", "truncated").times(1)),
    assertions: [TraceAssert.maxAttempts("search_flights", 1), TraceAssert.noErrors()],
  });

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.deepEqual(outputs, ['["AF123"', FLIGHTS]);
});

test("empty and returned outputs", async () => {
  const seen: unknown[] = [];
  await sdk().replayAsync(trace(), async (ctx) => {
    seen.push(await ctx.trackStepAsync("search_flights", "Paris", async () => "live"));
    seen.push(await ctx.trackStepAsync("search_flights", "Paris", async () => "live"));
    return "";
  }, mocked(
    ReplayFault.malformed("search_flights", "empty").times(1),
    ReplayFault.returns("search_flights", 42).times(1)));

  assert.deepEqual(seen, ["", 42]);
});

test("empty on a step whose recording isn't text is null", async () => {
  const t = trace();
  t.steps[0].output = ["AF123"];
  let seen: unknown = "unset";
  await sdk().replayAsync(t, async (ctx) => {
    seen = await ctx.trackStepAsync("search_flights", "Paris", async () => ["live"]);
    return "";
  }, mocked(ReplayFault.malformed("search_flights", "empty")));

  assert.equal(seen, null);
});

test("an every-call fault still fails on recordings before the faulted step", async () => {
  const t = trace();
  t.steps.unshift(recorded("load_profile", "Paris", "frequent flyer", "tool"));

  // A regression drops load_profile; the fault on search_flights must not excuse it.
  const result = await sdk().replayAsync(t, async (ctx) => {
    try { return await ctx.trackStepAsync("search_flights", "Paris", async () => "live"); }
    catch { return "Search is down"; }
  }, mocked(ReplayFault.httpError("search_flights", 503)));

  assert.equal(result.outcome, "failed");
  assert.equal(result.failures.length, 1);
  assert.match(result.failures[0], /never called 1 recorded step\(s\)/);
  assert.match(result.failures[0], /'load_profile' \(recorded step 1\)/);
});

test("a fault that never fires fails the replay", async () => {
  const result = await sdk().replayAsync(trace(), agent(0), mocked(ReplayFault.timeout("book_hotel")));

  assert.equal(result.outcome, "failed");
  assert.equal(result.failures.length, 1);
  assert.ok(result.failures[0].startsWith(
    "Fault not triggered: Timeout on 'book_hotel' never fired: the agent never called 'book_hotel'"));
  assert.equal(result.faults[0].triggered, false);
});

test("whenInput faults only matching calls", async () => {
  const result = await sdk().replayAsync(trace(), agent(0), mocked(ReplayFault.timeout("search_flights").whenInput("Rome")));

  assert.equal(result.failures.length, 1);
  assert.match(result.failures[0], /none of the 1 call\(s\) of 'search_flights' matched it/);
});

test("onCall skips earlier calls", async () => {
  const t = trace();
  t.steps.splice(1, 0, recorded("search_flights", "Paris", FLIGHTS, "tool"));
  const result = await sdk().replayAsync(t, async (ctx) => {
    await ctx.trackStepAsync("search_flights", "Paris", async () => "live");
    try { await ctx.trackStepAsync("search_flights", "Paris", async () => "live"); }
    catch { await ctx.trackStepAsync("search_flights", "Paris", async () => "live"); }
    return ctx.trackStepAsync("summarize", `flights: ${FLIGHTS}`, async () => "live");
  }, mocked(ReplayFault.timeout("search_flights").onCall(2).times(1)));

  assert.equal(result.outcome, "passed", result.failures.join("; "));
  assert.deepEqual(result.replayed_context!.steps.map((s) => s.status), ["success", "error", "success", "success"]);
});

test("maxAttempts: too many retries fail", async () => {
  const result = await sdk().replayAsync(trace(), agent(5), {
    ...mocked(ReplayFault.timeout("search_flights").times(3)),
    assertions: [TraceAssert.maxAttempts("search_flights", 2)],
  });

  assert.deepEqual(result.failures, ["MaxAttempts: expected at most 2 call(s) of 'search_flights' after its first failure, got 3"]);
});

test("maxAttempts: a step that never failed fails", async () => {
  const result = await sdk().replayAsync(trace(), agent(0), {
    mock_llm_responses: true, assertions: [TraceAssert.maxAttempts("search_flights", 2)],
  });

  assert.equal(result.failures.length, 1);
  assert.match(result.failures[0], /never failed/);
});

test("faults without a mocked or live replay throw", async () => {
  await assert.rejects(
    sdk().replayAsync(trace(), agent(0), { assertions: [], faults: [ReplayFault.timeout("search_flights")] }),
    /mocked or live replay/);
});

test("description names kind, step and scope", () => {
  assert.equal(ReplayFault.httpError("book", 503).times(2).description, "HTTP 503 on 'book' (first 2 calls)");
  assert.equal(ReplayFault.rateLimit("llm").description, "Rate limit (429, Retry-After 1s) on 'llm'");
  assert.equal(ReplayFault.returns("parse", 42).description, "Returned value on 'parse'");
  assert.throws(() => ReplayFault.httpError("book", 200), RangeError);
});

// --- Faults in scenarios -----------------------------------------------------------------------------------------

test("scenario: faults apply to trace-backed items and are reported", async () => {
  const result = await testSdk().withReplay(trace()).runScenarioAsync("travel", agent(1),
    [TraceAssert.maxAttempts("search_flights", 1)],
    [{ name: "recovers", trace_id: "tr_faults", assertions: [] }],
    { faults: [ReplayFault.timeout("search_flights").times(1)] });

  assert.equal(result.passed, true, result.summary);
  assert.deepEqual(result.results[0].faults!.map((f) => f.fired), [1]);

  const payload = ScenarioReport.runPayload(result, "synthetic") as { results: Array<{ faults: unknown[] }> };
  assert.deepEqual(payload.results[0].faults,
    [{ description: "Timeout on 'search_flights' (first call)", step_name: "search_flights", fired: 1 }]);
});

test("scenario: item faults add to scenario faults", async () => {
  const result = await testSdk().withReplay(trace()).runScenarioAsync("travel", agent(2), [],
    [{ trace_id: "tr_faults", assertions: [], faults: [ReplayFault.httpError("search_flights", 503).times(1)] }],
    { faults: [ReplayFault.timeout("search_flights").times(1)] });

  assert.equal(result.passed, true, result.summary);
  assert.deepEqual(result.results[0].faults!.map((f) => f.fired), [1, 1]);
});

test("scenario: an item inconclusive after a fault fails the run whatever the allowance", async () => {
  const result = await testSdk().withReplay(trace()).runScenarioAsync("travel", async (ctx) => {
    try {
      return await ctx.trackStepAsync("search_flights", "Paris", async () => "live");
    } catch {
      return ctx.trackStepAsync("search_trains", "Paris", async () => "live", { type: "tool" });
    }
  }, [],
    [{ name: "fallback", trace_id: "tr_faults", assertions: [], faults: [ReplayFault.httpError("search_flights", 500)] }],
    { max_inconclusive_rate: 1 });

  assert.equal(result.passed, false);
  assert.equal(result.inconclusive_count, 1);
  assert.equal(result.inconclusive_after_fault_count, 1);
  assert.equal(result.inconclusive_rate, 0);
  assert.equal(result.stayed_on_path_rate, null);
  assert.equal(result.results[0].inconclusive_after_fault, true);
  assert.equal(result.summary,
    "failed: inconclusive after a fault — 0 passed, 0 failed, 0 inconclusive, 1 inconclusive after a fault");

  const payload = ScenarioReport.runPayload(result, "synthetic") as {
    inconclusive_count: number; inconclusive_after_fault_count: number; results: Array<{ inconclusive: { reason: string } }>;
  };
  assert.equal(payload.inconclusive_count, 1);
  assert.equal(payload.inconclusive_after_fault_count, 1);
  assert.equal(payload.results[0].inconclusive.reason, "after_fault");
});

test("scenario: a synthetic item with faults fails", async () => {
  const result = await testSdk().runScenarioAsync("travel", agent(0), [],
    [{ name: "synthetic", input: "Paris", assertions: [], faults: [ReplayFault.timeout("search_flights")] }]);

  assert.equal(result.results[0].failures.length, 1);
  assert.match(result.results[0].failures[0], /needs a trace-backed item/);
});
