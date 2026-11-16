import { FaultReport, ReplayFault } from "./faults";

/** How a replayed call was bound to the recording. */
export type ReplayBindingKind =
  /** Served the recorded step with the same name and an identical input. */
  | "matched"
  /** No recorded step with that name had an identical input; served the next unused one in recorded order. */
  | "diverged"
  /** A step that ran for real: a model step in a live replay (`live_model_steps`), a tool in a tool-live replay (`live_tool_steps`). */
  | "live"
  /** Served an injected fault (`ReplayOptions.faults`) instead of running or using a recording. */
  | "faulted"
  /** Tool-live replay: a tool marked `reads_external_state`, served from the recording instead of running. Not checked. */
  | "not_replayed";

/**
 * Tool-live replay: one tool that ran for real, with its recorded and new outputs. `changed` when they differ (key
 * order and the step rule's `ignore_output_fields` aside). `actual_output` is the error message when the tool threw.
 */
export interface ToolOutputCheck {
  step_name: string;
  /** The 0-based position of the recording the call was compared with. */
  recorded_index: number;
  recorded_output: unknown;
  actual_output: unknown;
  changed: boolean;
  /** The failure for a changed output; null when the output matched. */
  message: string | null;
}

/** One replayed call and the recorded step bound to it. `recorded_index` is null for a live model call beyond the ones recorded. */
export interface ReplayBinding {
  step_name: string;
  kind: ReplayBindingKind;
  recorded_index: number | null;
  recorded_input: unknown;
  actual_input: unknown;
  /**
   * The type the call site gave (`StepOptions.type`) when it differs from the recorded type, or null. The recorded
   * type won: a mislabelled call can't turn a recorded tool step live.
   */
  declared_type?: string | null;
}

/** What a live replay does with a call whose input matches no unused recording of its step. */
export type OnDivergence =
  /** Stop the run at that step. The replay ends inconclusive: neither passed nor failed. */
  | "stop"
  /** Serve the next unused recording of that step and report the call as diverged in the fidelity report. */
  | "serve";

/** How replay matches calls of one step to its recordings, and what a live replay does when none matches. */
export interface ReplayStepRule {
  /**
   * Match calls on the step name alone, so a changed input never counts as divergence. For steps whose input
   * doesn't affect what they return, such as narration whose wording follows the model.
   */
  match_on_name_only?: boolean;
  /** Input property names dropped at any depth before matching (e.g. "reasoning", "request_id"). */
  ignore_input_fields?: string[];
  /**
   * Tool-live replay (`live_tool_steps`): output property names dropped at any depth before a tool's new output is
   * compared with its recorded output, for values that change on every call (ids, timestamps).
   */
  ignore_output_fields?: string[];
  /**
   * Tool-live replay: the tool reads state the recording doesn't hold (a live web page, a database, the clock), so
   * running it again can't be compared with the recording. It is served from the recording and reported as not
   * replayed, never as passed. Test such tools with real runs and assertions.
   */
  reads_external_state?: boolean;
  /**
   * Live replay: what a diverged call does. Unset uses `ReplayOptions.stop_on_divergence_types`: stop when the step
   * was recorded with one of those types, serve otherwise.
   */
  on_divergence?: OnDivergence;
}

/** Why a replay stopped. */
export type ReplayStopReason =
  /** The call's input matched no unused recording of its step, and the step stops on divergence. */
  | "diverged"
  /** The recording has no unused call of that step to serve, and it isn't a model step that can run live. */
  | "no_recording"
  /**
   * An injected fault (`ReplayOptions.faults`) sent the agent off its recording: this call matches no unused
   * recording. Past the fault the recording can't say what the real step would return.
   */
  | "after_fault";

/**
 * Where a live replay left its recording and stopped. Past this step nobody knows what the real tools would have
 * returned, so the replay is inconclusive: its assertions are not evaluated and it neither passes nor fails.
 */
export interface ReplayStop {
  /** The 1-based position of the call among the replay's steps. */
  step: number;
  step_name: string;
  reason: ReplayStopReason;
  /** The input of the recording the call was compared with; null when there was none. */
  recorded_input: unknown;
  actual_input: unknown;
  message: string;
}

/**
 * Thrown from a step when a live replay stops there (`ReplayStop`). Let it propagate: if the agent catches it, the
 * replay is still inconclusive, and every later step throws it again.
 */
export class ReplayStoppedError extends Error {
  readonly stop: ReplayStop;

  constructor(stop: ReplayStop) {
    super(stop.message);
    this.name = "ReplayStoppedError";
    this.stop = stop;
  }
}

/**
 * Thrown from a step when replay serves a recording of that step that failed in the recorded run, so the agent's retry
 * or fallback runs as it did then. It carries the recorded error message, not the original error's class: code that
 * only catches a specific error type won't catch it.
 */
export class RecordedStepError extends Error {
  readonly step_name: string;
  /** The error the recorded step failed with, or null when the recording didn't keep one. */
  readonly recorded_error: string | null;

  constructor(stepName: string, recordedError: string | null) {
    super(`Step '${stepName}' failed in the recorded run: ${recordedError ?? "no error message recorded"}`);
    this.name = "RecordedStepError";
    this.step_name = stepName;
    this.recorded_error = recordedError;
  }
}

/** A recorded step the replay never called. */
export interface UnusedRecording {
  step_name: string;
  recorded_index: number;
}

/**
 * How faithfully a mocked replay followed its recording. A replay is faithful when every call matched a
 * recorded step with an identical input and every recorded step was called.
 */
export class ReplayFidelity {
  private readonly _bindings: ReplayBinding[] = [];
  private _unused: UnusedRecording[];
  private _stop: ReplayStop | null = null;
  private readonly _faults: ReplayFault[];
  private readonly _fired: number[];
  private _firstFired: ReplayFault | null = null;
  /** Recordings from this index on may go unused: a fault that fails every call fired there. Null while none has. */
  private _unusedExcusedFrom: number | null = null;
  private readonly _neverExcused = new Set<number>();

  /** @internal */
  private readonly _toolChecks: ToolOutputCheck[] = [];
  /** The replay runs tools for real (`live_tool_steps`). */
  readonly live_tool_steps: boolean;

  constructor(recorded: UnusedRecording[], faults: ReplayFault[] = [], liveToolSteps = false) {
    this.live_tool_steps = liveToolSteps;
    this._unused = [...recorded];
    this._faults = [...faults];
    this._fired = faults.map(() => 0);
  }

  /** Each replayed call, in call order. */
  get bindings(): readonly ReplayBinding[] {
    return this._bindings;
  }

  /** Recorded steps no call consumed, in recorded order. */
  get unused(): readonly UnusedRecording[] {
    return this._unused;
  }

  get matched(): number {
    return this._bindings.filter((b) => b.kind === "matched").length;
  }

  get diverged(): number {
    return this._bindings.filter((b) => b.kind === "diverged").length;
  }

  /** Model steps that ran for real in a live replay. */
  get live(): number {
    return this._bindings.filter((b) => b.kind === "live").length;
  }

  /** Calls served an injected fault. */
  get faulted(): number {
    return this._bindings.filter((b) => b.kind === "faulted").length;
  }

  /** Each fault in `ReplayOptions.faults` and how many times it fired, in declared order. */
  get faults(): FaultReport[] {
    return this._faults.map((fault, i) => ({ fault, fired: this._fired[i], triggered: this._fired[i] > 0 }));
  }

  /** @internal The first fault that fired, or null while none has. */
  get firstFired(): ReplayFault | null {
    return this._firstFired;
  }

  /** @internal */
  get declaredFaults(): readonly ReplayFault[] {
    return this._faults;
  }

  /** @internal */
  firedCount(faultIndex: number): number {
    return this._fired[faultIndex];
  }

  /**
   * @internal `firstMatchingRecording`: the first unused recording the fault would have hit, or null; for a fault
   * that fails every call, recordings from there on are excused. `untargeted`: recordings of the faulted step the
   * fault would never have hit (other inputs, earlier calls); they are never excused, since the fault gives the agent
   * no reason to skip them.
   */
  fired(faultIndex: number, firstMatchingRecording: number | null, untargeted: number[]): void {
    this._fired[faultIndex]++;
    const fault = this._faults[faultIndex];
    this._firstFired ??= fault;
    if (fault.count !== null || this._fired[faultIndex] > 1) return;
    for (const index of untargeted) this._neverExcused.add(index);

    // A fault that fails every call sends the agent down its fallback path from here: recordings of the faulted step
    // and everything after it may rightly go unused. Recordings before it were the agent's to call.
    const used = this._bindings.filter((b) => b.recorded_index !== null).map((b) => b.recorded_index! + 1);
    const from = firstMatchingRecording ?? Math.max(0, ...used);
    this._unusedExcusedFrom = this._unusedExcusedFrom === null ? from : Math.min(this._unusedExcusedFrom, from);
  }

  /** No call diverged and every recorded step was called. Live calls don't count against it. */
  get is_faithful(): boolean {
    return this.diverged === 0 && this._unused.length === 0;
  }

  /** Where a live replay left its recording and stopped, or null when it didn't. */
  get stop(): ReplayStop | null {
    return this._stop;
  }

  /** @internal */
  stopped(stop: ReplayStop): void {
    this._stop ??= stop;
  }

  /** @internal */
  record(binding: ReplayBinding): void {
    this._bindings.push(binding);
    if (binding.recorded_index !== null)
      this._unused = this._unused.filter((u) => u.recorded_index !== binding.recorded_index);
  }

  /**
   * @internal The replay's binding failure, or null. A mocked replay fails when a recorded step was never called; a
   * live replay fails when no step ran live (a live model may legitimately skip recorded steps). Not called when the
   * agent threw, since the exception already explains the rest.
   */
  failure(traceId: string, liveModelSteps: boolean): string | null {
    if (liveModelSteps) return this.noLiveStepFailure(traceId);
    if (this.live_tool_steps) return this.noToolRanFailure(traceId) ?? this.unusedFailure(traceId);
    return this.unusedFailure(traceId);
  }

  /** Tool-live replay: each tool that ran for real and how its output compared, in call order. */
  get tool_checks(): readonly ToolOutputCheck[] {
    return this._toolChecks;
  }

  /** Tool-live replay: the tools whose new output differs from the recording. */
  get tool_output_changes(): ToolOutputCheck[] {
    return this._toolChecks.filter((c) => c.changed);
  }

  /** Tool-live replay: tools served from the recording because they read external state. */
  get not_replayed(): number {
    return this._bindings.filter((b) => b.kind === "not_replayed").length;
  }

  /** @internal */
  checked(check: ToolOutputCheck): void {
    this._toolChecks.push(check);
  }

  /** @internal The failure for a tool-live replay in which no tool ran, or null when at least one did. */
  noToolRanFailure(traceId: string): string | null {
    if (this._toolChecks.length > 0) return null;
    return (
      `Tool-live replay of trace ${traceId} ran no tool: none of the steps it called was recorded as type 'tool' ` +
      (this.not_replayed > 0 ? `except ${this.not_replayed} that read external state, ` : "") +
      'so no tool output was checked. Mark tool steps with { type: StepType.Tool } (or setMeta("type", "tool")) ' +
      "and record a new trace."
    );
  }

  /** @internal One failure per declared fault that never fired: a fault test whose fault didn't fire tested nothing. */
  unfiredFaultFailures(callsOf: (stepName: string) => number): string[] {
    return this._faults
      .filter((_, i) => this._fired[i] === 0)
      .map((f) => {
        const calls = callsOf(f.step_name);
        const why = calls === 0
          ? `the agent never called '${f.step_name}'`
          : `none of the ${calls} call(s) of '${f.step_name}' matched it`;
        return `Fault not triggered: ${f.description} never fired: ${why}. The failure path it was meant to test did not run.`;
      });
  }

  /**
   * @internal The failure for recorded steps that were never called, or null when every recording was used. After a
   * fault that fails every call, recordings from the faulted step on are excused: the agent took its fallback path.
   * Recordings of that step the fault doesn't target (another input, an earlier call) are not. A fault limited with
   * `times(n)` is expected to rejoin the recording.
   */
  unusedFailure(traceId: string): string | null {
    const from = this._unusedExcusedFrom;
    const unused = this._unused.filter(
      (u) => from === null || u.recorded_index < from || this._neverExcused.has(u.recorded_index));
    if (unused.length === 0) return null;
    const steps = unused.map((u) => `'${u.step_name}' (recorded step ${u.recorded_index + 1})`).join(", ");
    return (
      `Replay never called ${unused.length} recorded step(s) from trace ${traceId}: ${steps}. ` +
      "The agent skipped part of the recorded run, so its assertions may be passing on a path the recording does not cover."
    );
  }

  /** @internal The failure for a live replay in which nothing ran live, or null when at least one model step did. */
  noLiveStepFailure(traceId: string): string | null {
    if (this.live > 0) return null;
    return (
      `Live replay of trace ${traceId} ran no step live: none of the steps it called was recorded as type 'llm', so every ` +
      "output came from the recording and a prompt change could not show up. Mark model steps with " +
      'setMeta("type", "llm") and record a new trace, or replay in mocked mode.'
    );
  }
}
