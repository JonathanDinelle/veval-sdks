import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";
import { ReplayFidelity, ReplayStepRule, ReplayStop } from "./fidelity";
import { FaultReport, ReplayFault } from "./faults";
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
  /**
   * For a trace-backed item: `ReplayOptions.faults` for its replay, added after the scenario's `faults`. A synthetic
   * item (no `trace_id`) can't take faults and fails.
   */
  faults?: ReplayFault[];
}

export interface ScenarioOptions {
  /** Replay every trace-backed item fully mocked, as if each set `mock_model_steps`. `VevalTestSdk` always does. */
  mock_model_steps?: boolean;
  /** `ReplayOptions.step_rules` for every trace-backed item; an item's own entry for the same step replaces it. */
  step_rules?: Record<string, ReplayStepRule>;
  /**
   * The largest share of replayed items (0 to 1) that may be inconclusive in a passing run. Defaults to 0: any inconclusive item
   * fails the run, so a change that sends every item off its recorded path can't look green. Raise it to accept
   * some items needing a real run, e.g. 0.1 for one in ten.
   */
  max_inconclusive_rate?: number;
  /**
   * `ReplayOptions.faults` for every trace-backed item, before the item's own `faults`. Each item's replay gets its own
   * count, so a fault that never fires in an item fails that item. An item that stops after a fault is inconclusive
   * and always fails the run: the `max_inconclusive_rate` allowance is for live replays that leave their recording,
   * not for fault tests.
   */
  faults?: ReplayFault[];
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
  /** The item was a replay of a recorded trace (only a replay can be inconclusive). Set once the run completes. */
  replayed?: boolean;
  /** How the item ended: passed, failed, or inconclusive. Set once the run completes. */
  outcome?: ReplayOutcome;
  /** One line, e.g. `passed — 40/64 recorded steps used, 3 diverged but served, 12 live, stayed on path`. Set once the run completes. */
  summary?: string;
  /** Each fault injected into the item's replay and how many times it fired. Set once the run completes. */
  faults?: FaultReport[];
  /** The item stopped because an injected fault sent it off its recording (reason "after_fault"). Set once the run completes. */
  inconclusive_after_fault?: boolean;
}

export interface ScenarioRunResult {
  /**
   * True when no item failed, no item stopped after an injected fault, and the share of other inconclusive items is
   * within `max_inconclusive_rate` (by default, none may be inconclusive).
   */
  passed: boolean;
  pass_count: number;
  /** Items that failed. Inconclusive items are counted in `inconclusive_count`, not here. */
  fail_count: number;
  /** Items whose live replay left its recording and stopped, so they neither passed nor failed. */
  inconclusive_count: number;
  /**
   * Inconclusive items that stopped after an injected fault, included in `inconclusive_count`. They are left out of
   * `inconclusive_rate` and always fail the run: the fault test couldn't be judged, which no allowance should hide.
   */
  inconclusive_after_fault_count: number;
  /**
   * Inconclusive items as a share of replayed items, from 0 to 1, leaving out items that stopped after an injected
   * fault. Synthetic items don't dilute it.
   */
  inconclusive_rate: number;
  /** The `ScenarioOptions.max_inconclusive_rate` the run was judged against. */
  max_inconclusive_rate: number;
  /**
   * Replayed items that stayed on their recorded path (didn't stop), as a share of replayed items; null when no item
   * was replayed. How far live replay can be trusted for this change. Items that stopped after an injected fault are
   * left out: the fault sent them off the path.
   */
  stayed_on_path_rate: number | null;
  /** One line, e.g. `failed: too many inconclusive — 5 passed, 0 failed, 1 inconclusive (17% of replays > 0% allowed), 5/6 replays stayed on path`. */
  summary: string;
  results: ItemRunResult[];
}
