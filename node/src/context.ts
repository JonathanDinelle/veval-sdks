import { Step, StepHandle } from "./step";
import { StepData, TraceData } from "./tracing";
import { ReplayFidelity } from "./fidelity";
import { canonicalize } from "./snapshots";

export interface JudgeRecord {
  criteria: string;
  score: number;
  passed: boolean;
  reasoning: string;
}

interface RecordedStep {
  data: StepData;
  index: number;
  canonicalInput: string;
  consumed: boolean;
}

const NO_IGNORED_FIELDS = new Set<string>();

/**
 * Canonical text for matching a call to a recording. The live input is compared in the form it is sent in
 * (JSON.stringify), so a Date or an object with toJSON matches what the trace recorded for it.
 */
function inputKey(value: unknown): string {
  let sent = value;
  try {
    sent = value === undefined ? null : JSON.parse(JSON.stringify(value));
  } catch {
    // not JSON-serializable (e.g. a cycle): compare as-is
  }
  return canonicalize(sent, NO_IGNORED_FIELDS);
}

export class VevalExecutionContext {
  readonly traceId: string;
  readonly input: unknown;
  private _steps: Step[] = [];
  private _metadata: Record<string, unknown> = {};
  private _judgments: JudgeRecord[] = [];
  private _recorded: RecordedStep[] | null = null;
  private _fidelity: ReplayFidelity | null = null;
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

  /** How each replayed step was bound to the recording; null when the run is not a mocked replay. */
  get replayFidelity(): ReplayFidelity | null {
    return this._fidelity;
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
    if (this._recorded !== null) {
      const recorded = this.bindRecordedStep(name, input);
      if (recorded === null && this._strictMockMode) {
        const available = [...new Set(this._recorded.filter((r) => !r.consumed).map((r) => r.data.name))].join(", ");
        throw new Error(
          `Replay mode: no mock output for step '${name}'. ` +
          `Available steps: ${available}. ` +
          "This would have made a real LLM call."
        );
      }
      if (recorded !== null) {
        const mock = recorded.data.output;
        const s = new Step(name);
        s.input = input;
        // Keep the recorded type so toolCalled and snapshot types behave the same as in the original run.
        s.type = recorded.data.type ?? "custom";
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
    this._strictMockMode = strict;
    this._recorded = trace.steps.map((data, index) => ({
      data,
      index,
      canonicalInput: inputKey(data.input),
      consumed: false,
    }));
    this._fidelity = new ReplayFidelity(this._recorded.map((r) => ({ step_name: r.data.name, recorded_index: r.index })));
  }

  /**
   * Picks the recorded step to serve for a call: among unused recordings with the same name, the one whose
   * input is identical (key order ignored), otherwise the earliest — which is reported as diverged.
   */
  private bindRecordedStep(name: string, input: unknown): RecordedStep | null {
    const candidates = this._recorded!.filter((r) => !r.consumed && r.data.name === name);
    if (candidates.length === 0) return null;

    const actual = inputKey(input);
    const exact = candidates.find((r) => r.canonicalInput === actual);
    const recorded = exact ?? candidates[0];
    recorded.consumed = true;

    this._fidelity!.record({
      step_name: name,
      kind: exact ? "matched" : "diverged",
      recorded_index: recorded.index,
      recorded_input: recorded.data.input,
      actual_input: input,
    });
    return recorded;
  }
}
