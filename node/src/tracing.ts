import { Step } from "./step";
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
  project_id: string;
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

export interface SnapshotStep {
  name: string;
  output: unknown;
}

export interface SnapshotData {
  id?: string;
  step_names: string[];
  step_order: string[];
  step_count: number;
  steps: SnapshotStep[];
}

export interface SnapshotDiff {
  has_changes: boolean;
  added_steps: string[];
  removed_steps: string[];
  order_changes: string[];
}

export interface ReplayOptions {
  mock_llm_responses: boolean;
  assertions: ITraceAssertion[];
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
}

function collectSteps(steps: readonly Step[]): SnapshotStep[] {
  const result: SnapshotStep[] = [];
  for (const s of steps) {
    result.push({ name: s.name, output: s.output });
    result.push(...collectSteps(s.children));
  }
  return result;
}

export const SnapshotDataHelper = {
  fromContext(ctx: VevalExecutionContext): SnapshotData {
    const steps = collectSteps(ctx.steps);
    const names = steps.map((s) => s.name);
    return {
      step_names: [...new Set(names)],
      step_order: names,
      step_count: steps.length,
      steps,
    };
  },

  fromTrace(trace: TraceData): SnapshotData {
    const steps = trace.steps.map((s) => ({ name: s.name, output: s.output }));
    const names = steps.map((s) => s.name);
    return {
      id: trace.trace_id,
      step_names: [...new Set(names)],
      step_order: names,
      step_count: steps.length,
      steps,
    };
  },
};

export const SnapshotComparer = {
  compare(snapshot: SnapshotData, ctx: VevalExecutionContext): SnapshotDiff {
    const current = SnapshotDataHelper.fromContext(ctx);
    const snapshotSet = new Set(snapshot.step_names);
    const currentSet = new Set(current.step_names);

    const added_steps = current.step_names.filter((n) => !snapshotSet.has(n));
    const removed_steps = snapshot.step_names.filter((n) => !currentSet.has(n));

    const order_changes: string[] = [];
    const minLen = Math.min(snapshot.step_order.length, current.step_order.length);
    for (let i = 0; i < minLen; i++) {
      if (snapshot.step_order[i] !== current.step_order[i]) {
        order_changes.push(
          `Position ${i}: expected '${snapshot.step_order[i]}', got '${current.step_order[i]}'`
        );
      }
    }

    return {
      has_changes: added_steps.length > 0 || removed_steps.length > 0 || order_changes.length > 0,
      added_steps,
      removed_steps,
      order_changes,
    };
  },
};
