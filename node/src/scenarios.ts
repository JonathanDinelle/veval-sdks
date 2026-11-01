import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";
import { ReplayStepRule, ReplayStop } from "./fidelity";

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
}

export interface ItemRunResult {
  item: ScenarioItem;
  /** True only when the item passed; false when it failed or was inconclusive. */
  passed: boolean;
  failures: string[];
  /** Where the item's live replay left its recording and stopped, or null. When set, its assertions were not evaluated. */
  inconclusive?: ReplayStop | null;
  context?: VevalExecutionContext;
}

export interface ScenarioRunResult {
  /** True when every item passed: a failed or inconclusive item makes the run not pass. */
  passed: boolean;
  pass_count: number;
  /** Items that failed. Inconclusive items are counted in `inconclusive_count`, not here. */
  fail_count: number;
  /** Items whose live replay left its recording and stopped, so they neither passed nor failed. */
  inconclusive_count: number;
  results: ItemRunResult[];
}
