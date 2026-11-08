import type { SnapshotDiff, SnapshotOptions } from "./snapshots";
import { VevalExecutionContext } from "./context";
import { ITraceAssertion } from "./assertions";
import type { ReplayFidelity, ReplayStepRule, ReplayStop } from "./fidelity";

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
  /**
   * Per step name: how calls match their recordings (name only, or with some input fields ignored) and, in a
   * live replay, whether a diverged call stops the run or is served. Matching rules apply to mocked replays too.
   */
  step_rules?: Record<string, ReplayStepRule>;
  /**
   * Live replay: steps recorded with one of these types stop the run when a call diverges, unless their
   * `step_rules` entry says otherwise. A stopped replay is inconclusive (`ReplayResult.inconclusive`): past that
   * step the recording can't say what the real tool would have returned. Defaults to `["tool"]`; other diverged
   * steps are served the next unused recording and reported in `fidelity`.
   */
  stop_on_divergence_types?: string[];
}

/** How a replay ended. `inconclusive`: a live replay left its recording and stopped; see `ReplayResult.inconclusive`. */
export type ReplayOutcome = "passed" | "failed" | "inconclusive";

export interface ReplayResult {
  /** True only when the replay passed; false when it failed or was inconclusive. */
  passed: boolean;
  outcome: ReplayOutcome;
  /**
   * Where a live replay stopped because a call left the recording, or null. When set, assertions were not
   * evaluated and `failures` is empty: the replay can't back up a pass or a fail.
   */
  inconclusive: ReplayStop | null;
  failures: string[];
  replayed_context: VevalExecutionContext | null;
  output: unknown;
  started_at: string;
  completed_at: string;
  /** The replay trace's status: "success", "error" (the agent threw), or "inconclusive" (the replay stopped). */
  status: string;
  error: string | null;
  /** One line, e.g. `passed — 40/64 recorded steps used, 3 diverged, 12 live, stayed on path`. */
  summary: string;
  /** How the replay differed from its recording; set when `compare_with_recording` is on. */
  recording_diff?: SnapshotDiff | null;
  /** How each call was bound to the recording; set for mocked and live replays (`mock_llm_responses` or `live_model_steps`). */
  fidelity?: ReplayFidelity | null;
}
