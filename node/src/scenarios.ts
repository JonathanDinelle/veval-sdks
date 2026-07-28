import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";

export interface ScenarioItem {
  name?: string;
  trace_id?: string;
  input?: unknown;
  assertions: ITraceAssertion[];
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
