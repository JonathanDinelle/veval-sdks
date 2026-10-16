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
   */
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
