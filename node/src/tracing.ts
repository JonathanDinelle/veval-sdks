import type { SnapshotDiff, SnapshotOptions } from "./snapshots";
import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";
import type { ReplayFidelity } from "./fidelity";

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
  /**
   * Serve every step's recorded output instead of running it. Deterministic and free, so it checks control
   * flow and code changes; it can't detect a prompt regression, because model calls return the old outputs.
   */
  mock_llm_responses: boolean;
  /**
   * Run steps recorded as `llm` for real and serve every other step (tools, untyped steps) from the
   * recording, so a prompt change shows up while tools never reach a real system. Takes precedence over
   * `mock_llm_responses`. The replay fails if no step ran live.
   */
  live_model_steps?: boolean;
  assertions: ITraceAssertion[];
  /**
   * When set, the replay also fails if its step sequence or step inputs drifted from the recorded trace.
   * Without it, a call whose input differs from every recording of that step is still served (the next
   * unused recording in order) and only reported in `fidelity`.
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
  /** How each call was bound to the recording; set when `mock_llm_responses` is on. */
  fidelity?: ReplayFidelity | null;
}
