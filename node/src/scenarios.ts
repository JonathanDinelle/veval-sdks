import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";

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
}

export interface ScenarioOptions {
  /** Replay every trace-backed item fully mocked, as if each set `mock_model_steps`. `VevalTestSdk` always does. */
  mock_model_steps?: boolean;
}

export interface ItemRunResult {
  item: ScenarioItem;
  passed: boolean;
  failures: string[];
  context?: VevalExecutionContext;
}

export interface ScenarioRunResult {
  passed: boolean;
  pass_count: number;
  fail_count: number;
  results: ItemRunResult[];
}
