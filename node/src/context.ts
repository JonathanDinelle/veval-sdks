import { Step, StepHandle } from "./step";
import { StepData, TraceData } from "./tracing";

export interface JudgeRecord {
  criteria: string;
  score: number;
  passed: boolean;
  reasoning: string;
}

export class VevalExecutionContext {
  readonly traceId: string;
  readonly input: unknown;
  private _steps: Step[] = [];
  private _metadata: Record<string, unknown> = {};
  private _judgments: JudgeRecord[] = [];
  private _mockOutputs: Map<string, StepData[]> | null = null;
  private _strictMockMode = false;

  constructor(traceId: string, input: unknown) {
    this.traceId = traceId;
    this.input = input;
  }

  get steps(): readonly Step[] {
    return this._steps;
  }

  get traceMeta(): Readonly<Record<string, unknown>> {
    return this._metadata;
  }

  get judgments(): readonly JudgeRecord[] {
    return this._judgments;
  }

  setMetadata(key: string, value: unknown): void {
    this._metadata[key] = value;
  }

  recordJudgment(criteria: string, score: number, passed: boolean, reasoning: string): void {
    this._judgments.push({ criteria, score, passed, reasoning });
  }

  async trackStepAsync<T>(
    name: string,
    input: unknown,
    fn: ((handle: StepHandle) => Promise<T>) | (() => Promise<T>)
  ): Promise<T> {
    if (this._mockOutputs !== null && this._strictMockMode) {
      const q = this._mockOutputs.get(name);
      if (!q || q.length === 0) {
        const available = [...(this._mockOutputs?.keys() ?? [])].join(", ");
        throw new Error(
          `Replay mode: no mock output for step '${name}'. ` +
          `Available steps: ${available}. ` +
          "This would have made a real LLM call."
        );
      }
    }

    if (this._mockOutputs !== null) {
      const q = this._mockOutputs.get(name);
      if (q && q.length > 0) {
        const recorded = q.shift()!;
        const mock = recorded.output;
        const s = new Step(name);
        s.input = input;
        // Keep the recorded type so toolCalled and snapshot types behave the same as in the original run.
        s.type = recorded.type ?? "custom";
        s.metadata["_source"] = "replay";
        s.complete(mock);
        this._steps.push(s);
        return mock as T;
      }
    }

    const s = new Step(name);
    s.input = input;
    this._steps.push(s);
    const handle = new StepHandle(s);

    try {
      const result =
        fn.length === 0
          ? await (fn as () => Promise<T>)()
          : await (fn as (h: StepHandle) => Promise<T>)(handle);
      s.complete(result);
      return result;
    } catch (err) {
      const msg = err instanceof Error ? err.message : String(err);
      s.fail(msg);
      throw err;
    }
  }

  loadMockOutputs(trace: TraceData, strict = true): void {
    if (!trace.steps || trace.steps.length === 0) {
      throw new Error(
        `Replay trace '${trace.trace_id}' has no steps. Cannot mock LLM calls. ` +
        "Ensure the trace was recorded with steps before using it for replay."
      );
    }
    this._mockOutputs = new Map();
    this._strictMockMode = strict;
    for (const step of trace.steps) {
      if (!this._mockOutputs.has(step.name)) {
        this._mockOutputs.set(step.name, []);
      }
      this._mockOutputs.get(step.name)!.push(step);
    }
  }
}
