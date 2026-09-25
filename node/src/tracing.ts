import type { SnapshotDiff, SnapshotOptions } from "./snapshots";
import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";

export interface StepData {
  step_id: string;
  parent_step_id: string | null;
  name: string;
  type: string;
  input: unknown;
  output: unknown;
  status: string;
  error: string | null;
  started_at: string;
  completed_at: string | null;
  duration_ms: number | null;
  cost_usd: number | null;
  tokens_in: number | null;
  tokens_out: number | null;
  model: string | null;
  metadata: Record<string, unknown>;
}

export interface TraceData {
  trace_id: string;
  agent_name: string;
  /** Present on traces loaded from older API versions; no longer sent. */
  project_id?: string;
  input: unknown;
  output: unknown;
  status: string;
  error: string | null;
  started_at: string;
  completed_at: string | null;
  duration_ms: number | null;
  total_cost_usd: number | null;
  metadata: Record<string, unknown>;
  steps: StepData[];
}

export interface ReplayOptions {
  mock_llm_responses: boolean;
  assertions: ITraceAssertion[];
  /**
   * When set, the replay also fails if its step sequence or step inputs drifted from the recorded trace.
   * Recorded outputs are served by step name, so a control-flow change can bind an output to the wrong
   * call and still pass every assertion — this check catches that.
   */
  compare_with_recording?: SnapshotOptions;
}

export interface ReplayResult {
  passed: boolean;
  failures: string[];
  replayed_context: VevalExecutionContext | null;
  output: unknown;
  started_at: string;
  completed_at: string;
  status: string;
  error: string | null;
  /** How the replay differed from its recording; set when `compare_with_recording` is on. */
  recording_diff?: SnapshotDiff | null;
}
