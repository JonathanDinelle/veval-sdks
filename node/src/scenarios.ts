import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";
import { ReplayFidelity, ReplayStepRule, ReplayStop } from "./fidelity";
import { ReplayOutcome } from "./tracing";

export interface ScenarioItem {
  name?: string;
  trace_id?: string;
  input?: unknown;
  assertions: ITraceAssertion[];
  /**
   * For a trace-backed item: serve model steps from the recording too (mocked replay) instead of running
   * them live. Cheaper and deterministic, so it suits code changes, but it can't catch a prompt regression.
   * `VevalTestSdk` always replays mocked; for items loaded from the dashboard, use `ScenarioOptions.mock_model_steps`.
   */
  mock_model_steps?: boolean;
  /** For a trace-backed item: `ReplayOptions.step_rules` for its replay. Replaces the scenario-level rule of the same step. */
  step_rules?: Record<string, ReplayStepRule>;
}

export interface ScenarioOptions {
  /** Replay every trace-backed item fully mocked, as if each set `mock_model_steps`. `VevalTestSdk` always does. */
  mock_model_steps?: boolean;
  /** `ReplayOptions.step_rules` for every trace-backed item; an item's own entry for the same step replaces it. */
  step_rules?: Record<string, ReplayStepRule>;
  /**
   * The largest share of inconclusive items (0 to 1) a passing run may have. Defaults to 0: any inconclusive item
   * fails the run, so a change that sends every item off its recorded path can't look green. Raise it to accept
   * some items needing a real run, e.g. 0.1 for one in ten.
   */
  max_inconclusive_rate?: number;
}

/** @internal `ScenarioOptions.max_inconclusive_rate`, checked: a share from 0 to 1, 0 when unset. */
export function maxInconclusiveRate(options?: ScenarioOptions): number {
  const rate = options?.max_inconclusive_rate ?? 0;
  if (!(rate >= 0 && rate <= 1)) throw new RangeError(`max_inconclusive_rate is a share from 0 to 1, got ${rate}.`);
  return rate;
}

export interface ItemRunResult {
  item: ScenarioItem;
  /** True only when the item passed; false when it failed or was inconclusive. */
  passed: boolean;
  failures: string[];
  /** Where the item's live replay left its recording and stopped, or null. When set, its assertions were not evaluated. */
  inconclusive?: ReplayStop | null;
  /** How the item's replay was bound to its recording; unset for a synthetic item or a missing trace. */
  fidelity?: ReplayFidelity | null;
  context?: VevalExecutionContext;
  /** How the item ended: passed, failed, or inconclusive. Set once the run completes. */
  outcome?: ReplayOutcome;
  /** One line, e.g. `passed — 40/64 recorded steps used, 3 diverged, 12 live, stayed on path`. Set once the run completes. */
  summary?: string;
}

export interface ScenarioRunResult {
  /**
   * True when no item failed and the share of inconclusive items is within `max_inconclusive_rate` (by default,
   * none may be inconclusive).
   */
  passed: boolean;
  pass_count: number;
  /** Items that failed. Inconclusive items are counted in `inconclusive_count`, not here. */
  fail_count: number;
  /** Items whose live replay left its recording and stopped, so they neither passed nor failed. */
  inconclusive_count: number;
  /** Inconclusive items as a share of all items, from 0 to 1. */
  inconclusive_rate: number;
  /** The `ScenarioOptions.max_inconclusive_rate` the run was judged against. */
  max_inconclusive_rate: number;
  /**
   * Replayed items that stayed on their recorded path (didn't stop), as a share of replayed items; null when no item
   * was replayed. How far live replay can be trusted for this change.
   */
  stayed_on_path_rate: number | null;
  /** One line, e.g. `failed — 5 passed, 0 failed, 1 inconclusive (17% > 0% allowed), 5/6 replays stayed on path`. */
  summary: string;
  results: ItemRunResult[];
}
