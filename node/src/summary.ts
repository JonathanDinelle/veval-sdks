import { ReplayFidelity, ReplayStop } from "./fidelity";

/**
 * @internal The one-line summary on a replay or item result, e.g.
 * `passed — 40/64 recorded steps used, 3 diverged but served, 12 live, stayed on path`.
 */
export function replaySummary(failures: string[], stop: ReplayStop | null | undefined, fidelity: ReplayFidelity | null | undefined): string {
  const head = stop
    ? `inconclusive at step ${stop.step} '${stop.step_name}'`
    : failures.length === 0 ? "passed" : `failed (${count(failures.length, "failure")})`;
  if (!fidelity) return head;

  const used = new Set(fidelity.bindings.filter((b) => b.recorded_index !== null).map((b) => b.recorded_index)).size;
  const recorded = used + fidelity.unused.length;
  return `${head} — ${used}/${recorded} recorded steps used, ${fidelity.diverged} diverged but served, ${fidelity.live} live, ` +
    (stop ? "left the path" : "stayed on path");
}

/** @internal The one-line summary on a scenario run result. */
export function scenarioSummary(r: {
  passed: boolean;
  pass_count: number;
  fail_count: number;
  inconclusive_count: number;
  inconclusive_rate: number;
  max_inconclusive_rate: number;
  replayed: number;
  stayed_on_path: number;
}): string {
  const tooManyInconclusive = r.inconclusive_rate > r.max_inconclusive_rate;
  // The reason comes first: "failed ... 0 failed" would otherwise read like a bug.
  const head = r.passed ? "passed"
    : r.fail_count === 0 && tooManyInconclusive ? "failed: too many inconclusive"
    : "failed";
  let line = `${head} — ${r.pass_count} passed, ${r.fail_count} failed, ${r.inconclusive_count} inconclusive`;
  if (r.inconclusive_count > 0)
    line += tooManyInconclusive
      ? ` (${percent(r.inconclusive_rate)} of replays > ${percent(r.max_inconclusive_rate)} allowed)`
      : ` (${percent(r.inconclusive_rate)} of replays, within ${percent(r.max_inconclusive_rate)} allowed)`;
  if (r.replayed > 0) line += `, ${r.stayed_on_path}/${r.replayed} replays stayed on path`;
  return line;
}

function count(n: number, noun: string): string {
  return n === 1 ? `1 ${noun}` : `${n} ${noun}s`;
}

function percent(rate: number): string {
  return `${Math.floor(rate * 100 + 0.5)}%`;
}
