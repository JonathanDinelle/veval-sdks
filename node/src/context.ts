import { Step, StepHandle, StepOptions } from "./step";
import { StepData, TraceData } from "./tracing";
import { ReplayFidelity, ReplayStepRule, ReplayStop, ReplayStopReason, ReplayStoppedError } from "./fidelity";
import { canonicalize } from "./snapshots";
import { ReplayFault } from "./faults";

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
  /** Matching keys with some fields dropped, by sorted field list; computed once per field set. */
  keysIgnoring: Map<string, string>;
}

const NO_IGNORED_FIELDS = new Set<string>();

/**
 * Canonical text for matching a call to a recording. The live input is compared in the form it is sent in
 * (JSON.stringify), so a Date or an object with toJSON matches what the trace recorded for it.
 */
function inputKey(value: unknown, ignoreFields: Set<string> = NO_IGNORED_FIELDS): string {
  let sent = value;
  try {
    sent = value === undefined ? null : JSON.parse(JSON.stringify(value));
  } catch {
    // not JSON-serializable (e.g. a cycle): compare as-is
  }
  return canonicalize(sent, ignoreFields);
}

/**
 * The matching key of an input with some fields dropped. An input that is a JSON object or array encoded as a string,
 * such as a model's tool-call arguments, is parsed first, so its fields can be dropped too.
 */
function ignoringKey(value: unknown, ignoreFields: Set<string>): string {
  if (typeof value === "string" && /^\s*[[{]/.test(value)) {
    try {
      return inputKey(JSON.parse(value), ignoreFields);
    } catch {
      // not JSON after all: compare the text
    }
  }
  return inputKey(value, ignoreFields);
}

function keyIgnoring(recorded: RecordedStep, ignore: string[]): string {
  const cacheKey = ignore.join("\u0001");
  let key = recorded.keysIgnoring.get(cacheKey);
  if (key === undefined) {
    key = ignoringKey(recorded.data.input, new Set(ignore));
    recorded.keysIgnoring.set(cacheKey, key);
  }
  return key;
}

/** An input as compact JSON for a message, as the C# and Python SDKs print it. */
function oneLine(value: unknown): string {
  try {
    return JSON.stringify(value === undefined ? null : value) ?? String(value);
  } catch {
    return String(value);
  }
}

/** Options for a replay's step binding: matching rules, and which diverged steps stop a live replay. @internal */
export interface StepBindingOptions {
  step_rules?: Record<string, ReplayStepRule>;
  stop_on_divergence_types?: string[];
  faults?: ReplayFault[];
}

/** Whether replay served this step an injected fault (`ReplayOptions.faults`). @internal */
export function isInjectedFault(step: Step): boolean {
  return step.metadata["_source"] === "fault";
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
  private _liveModelSteps = false;
  private _stepRules: Record<string, ReplayStepRule> = {};
  private _stopOnDivergenceTypes = new Set<string>();
  private _isReplay = false;
  private _callCounts = new Map<string, number>();

  constructor(traceId: string, input: unknown) {
    this.traceId = traceId;
    this.input = input;
  }

  /**
   * True when this run replays a recorded trace: `replayAsync`, a trace-backed scenario item, or
   * `VevalTestSdk.withReplay`, mocked or live. Check it before a side effect that isn't inside a step
   * served from the recording, such as sending an email; in a live replay, model steps run for real.
   */
  get isReplay(): boolean {
    return this._isReplay;
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

  /** How each replayed step was bound to the recording; null when the run doesn't replay a recording. */
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
    fn: ((handle: StepHandle) => Promise<T>) | (() => Promise<T>),
    options?: StepOptions
  ): Promise<T> {
    const declaredType = options?.type ?? null;
    let effectiveType: string | null = declaredType;
    if (this._recorded !== null) {
      // Once a live replay has stopped, nothing after it can be served truthfully, even if the agent caught the stop.
      const stopped = this._fidelity!.stop;
      if (stopped) throw new ReplayStoppedError(stopped);

      const rule = Object.prototype.hasOwnProperty.call(this._stepRules, name) ? this._stepRules[name] : undefined;
      const call = (this._callCounts.get(name) ?? 0) + 1;
      this._callCounts.set(name, call);
      const faultIndex = this.faultFor(name, input, call, rule);
      if (faultIndex !== null) return this.serveFault<T>(name, input, faultIndex, rule, declaredType);

      const { recorded, exact } = this.findRecording(name, input, rule);
      const recordedType = recorded?.data.type ?? this.recordedTypeOf(name);
      // A recorded llm or tool type wins; a recorded custom type is what an untyped step is saved as, so it says
      // nothing and the call site's type applies. A mislabelled call still can't turn a recorded tool step live.
      const knownType = recordedType && recordedType !== "custom" ? recordedType : null;
      effectiveType = knownType ?? declaredType ?? recordedType ?? null;
      // In live mode a model step runs for real, including extra calls of a step recorded as a model step and
      // model steps the recording never contained.
      const runLive = this._liveModelSteps && effectiveType === "llm";
      const conflictingType = knownType && declaredType && declaredType !== knownType ? declaredType : null;

      // After a fault, a mocked replay serves only calls that still match their recording: a fallback the
      // recording never called, or a model call whose prompt now carries the error, has no truthful output.
      if (!this._liveModelSteps && this._fidelity!.firstFired !== null && (recorded === null || !exact))
        throw this.stopAt(name, recorded === null ? "no_recording" : "diverged", recorded?.data.input ?? null, input, effectiveType);

      if (recorded === null && !runLive && this._strictMockMode) {
        if (this._liveModelSteps) throw this.stopAt(name, "no_recording", null, input, effectiveType);
        const available = [...new Set(this._recorded.filter((r) => !r.consumed).map((r) => r.data.name))].join(", ");
        throw new Error(
          `Replay mode: no mock output for step '${name}'. ` +
          `Available steps: ${available}. ` +
          "This would have made a real LLM call."
        );
      }

      // A live model handed a recording from another path would reason over a page it never reached.
      if (this._liveModelSteps && !runLive && recorded !== null && !exact && this.stopsOnDivergence(rule, effectiveType))
        throw this.stopAt(name, "diverged", recorded.data.input, input, effectiveType);
      if (recorded !== null) recorded.consumed = true;

      if (runLive) {
        this._fidelity!.record({
          step_name: name, kind: "live", recorded_index: recorded?.index ?? null,
          recorded_input: recorded?.data.input ?? null, actual_input: input, declared_type: conflictingType,
        });
      } else if (recorded !== null) {
        this._fidelity!.record({
          step_name: name, kind: exact ? "matched" : "diverged", recorded_index: recorded.index,
          recorded_input: recorded.data.input, actual_input: input, declared_type: conflictingType,
        });
        const mock = recorded.data.output;
        const s = new Step(name);
        s.input = input;
        // The recorded type, or the call site's when the recording saved the step untyped, so toolCalled and
        // snapshot types match the original run wherever the recording knew the step's type.
        s.type = effectiveType ?? "custom";
        s.metadata["_source"] = "replay";
        s.complete(mock);
        this._steps.push(s);
        return mock as T;
      }
    }

    const s = new Step(name);
    s.input = input;
    s.type = effectiveType ?? "custom";
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

  /**
   * `liveModelSteps`: run steps recorded as `llm` for real and serve every other step from the recording.
   * `options`: step matching rules, and which diverged steps stop a live replay.
   */
  loadMockOutputs(trace: TraceData, strict = true, liveModelSteps = false, options?: StepBindingOptions): void {
    if (!trace.steps || trace.steps.length === 0) {
      throw new Error(
        `Replay trace '${trace.trace_id}' has no steps. Cannot mock LLM calls. ` +
        "Ensure the trace was recorded with steps before using it for replay."
      );
    }
    this._isReplay = true;
    this._strictMockMode = strict;
    this._liveModelSteps = liveModelSteps;
    this._stepRules = options?.step_rules ?? {};
    this._stopOnDivergenceTypes = new Set(options?.stop_on_divergence_types ?? ["tool"]);
    this._recorded = trace.steps.map((data, index) => ({
      data,
      index,
      canonicalInput: inputKey(data.input),
      consumed: false,
      keysIgnoring: new Map<string, string>(),
    }));
    this._fidelity = new ReplayFidelity(
      this._recorded.map((r) => ({ step_name: r.data.name, recorded_index: r.index })), options?.faults ?? []);
  }

  /** The index of the first declared fault that applies to this call of the step, or null. */
  private faultFor(name: string, input: unknown, call: number, rule: ReplayStepRule | undefined): number | null {
    const faults = this._fidelity!.declaredFaults;
    for (let i = 0; i < faults.length; i++) {
      const fault = faults[i];
      if (fault.step_name !== name || call < fault.from_call) continue;
      if (fault.count !== null && this._fidelity!.firedCount(i) >= fault.count) continue;
      if (fault.has_input) {
        const ignore = new Set(rule?.ignore_input_fields ?? []);
        if (ignoringKey(fault.input, ignore) !== ignoringKey(input, ignore)) continue;
      }
      return i;
    }
    return null;
  }

  /**
   * Serves a fault at this call: records it as a step with `_source = "fault"` and either throws the fault's error or
   * returns its malformed output. The call uses up no recording, so a retry is served the recorded one.
   */
  private serveFault<T>(
    name: string, input: unknown, faultIndex: number, rule: ReplayStepRule | undefined, declaredType: string | null
  ): T {
    const fidelity = this._fidelity!;
    const fault = fidelity.declaredFaults[faultIndex];
    fidelity.fired(faultIndex);
    fidelity.record({ step_name: name, kind: "faulted", recorded_index: null, recorded_input: null, actual_input: input });

    const recordedType = this.recordedTypeOf(name);
    const type = !recordedType || recordedType === "custom" ? declaredType ?? recordedType : recordedType;
    const s = new Step(name);
    s.input = input;
    s.type = type ?? "custom";
    s.metadata["_source"] = "fault";
    s.metadata["_fault"] = fault.description;
    this._steps.push(s);

    if (fault.kind !== "malformed" && fault.kind !== "returns") {
      const error = fault.createError();
      s.fail(error instanceof Error ? error.message : String(error));
      throw error;
    }

    let value: unknown;
    try {
      value = this.malformedOutputOf(fault, name, input, rule);
    } catch (err) {
      s.fail(err instanceof Error ? err.message : String(err));
      throw err;
    }
    s.complete(value);
    return value as T;
  }

  private malformedOutputOf(fault: ReplayFault, name: string, input: unknown, rule: ReplayStepRule | undefined): unknown {
    if (fault.kind === "returns") return fault.value;
    const { recorded } = this.findRecording(name, input, rule);
    const output = recorded?.data.output;
    if (fault.malformed_output === "empty") return typeof output === "string" ? "" : null;
    if (!recorded)
      throw new Error(
        `Fault on '${name}': there is no unused recording of the step to truncate. Give the value with ReplayFault.returns instead.`
      );
    const text = typeof output === "string" ? output : JSON.stringify(output ?? null);
    return text.substring(0, Math.floor(text.length / 2));
  }

  /**
   * Picks the recorded step for a call: among unused recordings with the same name, the one whose input is
   * identical (key order and the rule's ignored fields aside), otherwise the earliest — which is reported as
   * diverged. A name-only rule matches the earliest.
   */
  private findRecording(
    name: string, input: unknown, rule: ReplayStepRule | undefined
  ): { recorded: RecordedStep | null; exact: boolean } {
    const candidates = this._recorded!.filter((r) => !r.consumed && r.data.name === name);
    if (candidates.length === 0) return { recorded: null, exact: false };
    if (rule?.match_on_name_only) return { recorded: candidates[0], exact: true };

    const ignore = [...new Set(rule?.ignore_input_fields ?? [])].sort();
    const actual = ignore.length === 0 ? inputKey(input) : ignoringKey(input, new Set(ignore));
    const match = candidates.find((r) => (ignore.length === 0 ? r.canonicalInput : keyIgnoring(r, ignore)) === actual);
    return { recorded: match ?? candidates[0], exact: match !== undefined };
  }

  /** Whether a diverged call of this step stops a live replay: the step's rule decides, else its type. */
  private stopsOnDivergence(rule: ReplayStepRule | undefined, recordedType: string | null | undefined): boolean {
    if (rule?.on_divergence) return rule.on_divergence === "stop";
    return !!recordedType && this._stopOnDivergenceTypes.has(recordedType);
  }

  /**
   * Stops a replay at this call: records the stop and a stopped step for it, and returns the error that ends the run.
   * `kind` says whether the step had unused recordings with other inputs ("diverged") or none ("no_recording"). Once a
   * fault has fired, the stop's reason is "after_fault": the fault, not the agent, sent the run off its recording.
   */
  private stopAt(
    name: string, kind: "diverged" | "no_recording", recordedInput: unknown, input: unknown, recordedType: string | null | undefined
  ): ReplayStoppedError {
    const position = this._steps.length + 1;
    const fault = this._fidelity!.firstFired;
    const reason: ReplayStopReason = fault ? "after_fault" : kind;
    const detail =
      kind === "diverged"
        ? `its input matches no unused recording of '${name}', so what the real step would return is unknown. ` +
          `Recorded input: ${oneLine(recordedInput)}. Actual input: ${oneLine(input)}.`
        : `the recording has no unused call of '${name}' to serve (${this.recordedCallsOf(name)}).`;
    let advice =
      kind === "diverged"
        ? " If the model took a different path, record that path. If your code changed how the step is called, " +
          "re-record, or replay mocked to test this change. If this input doesn't affect the step's output, " +
          "match it on name only (step_rules)."
        : " If the model took a different path, record that path. If your code added this call, re-record, or " +
          "replay mocked to test this change.";
    if (fault)
      advice = " Past the fault the agent took a path the recording doesn't cover (a fallback it never called, or a " +
        "call whose input now carries the error), so no recorded output can stand in for it. " +
        (this._liveModelSteps ? "Record a trace of this path." : "Replay live to run model steps for real, or record a trace of this path.");
    const where = fault ? ` after an injected fault (${fault.description})` : "";
    const message =
      `${this._liveModelSteps ? "Live replay" : "Replay"} stopped at step ${position} ('${name}')${where}: ${detail} ` +
      "The replay is inconclusive: it neither passed nor failed." + advice;

    const stop: ReplayStop = {
      step: position, step_name: name, reason,
      recorded_input: recordedInput ?? null, actual_input: input ?? null, message,
    };
    this._fidelity!.stopped(stop);

    const s = new Step(name);
    s.input = input;
    s.type = recordedType ?? "custom";
    s.metadata["_source"] = "replay";
    s.stopped(message);
    this._steps.push(s);
    return new ReplayStoppedError(stop);
  }

  private recordedCallsOf(name: string): string {
    const count = this._recorded!.filter((r) => r.data.name === name).length;
    return count === 0 ? "the trace never recorded it" : `its ${count} recorded call(s) were already used`;
  }

  /** The recorded type of a step name, used and unused alike; null when the trace never recorded it. */
  private recordedTypeOf(name: string): string | null {
    return this._recorded!.find((r) => r.data.name === name)?.data.type ?? null;
  }
}

/** Marks a run as a replay of a recorded trace. Module-level and not exported from the package, so user code can't flip it. */
export function markAsReplay(ctx: VevalExecutionContext): void {
  (ctx as unknown as { _isReplay: boolean })._isReplay = true;
}
