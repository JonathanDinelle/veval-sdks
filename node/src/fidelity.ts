/** How a replayed call was bound to the recording. */
export type ReplayBindingKind =
  /** Served the recorded step with the same name and an identical input. */
  | "matched"
  /** No recorded step with that name had an identical input; served the next unused one in recorded order. */
  | "diverged"
  /** A model step that ran for real in a live replay (`live_model_steps`). */
  | "live";

/** One replayed call and the recorded step bound to it. `recorded_index` is null for a live model call beyond the ones recorded. */
export interface ReplayBinding {
  step_name: string;
  kind: ReplayBindingKind;
  recorded_index: number | null;
  recorded_input: unknown;
  actual_input: unknown;
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

  /** @internal */
  constructor(recorded: UnusedRecording[]) {
    this._unused = [...recorded];
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

  /** No call diverged and every recorded step was called. Live calls don't count against it. */
  get is_faithful(): boolean {
    return this.diverged === 0 && this._unused.length === 0;
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
    return liveModelSteps ? this.noLiveStepFailure(traceId) : this.unusedFailure(traceId);
  }

  /** @internal The failure for recorded steps that were never called, or null when every recording was used. */
  unusedFailure(traceId: string): string | null {
    if (this._unused.length === 0) return null;
    const steps = this._unused.map((u) => `'${u.step_name}' (recorded step ${u.recorded_index + 1})`).join(", ");
    return (
      `Replay never called ${this._unused.length} recorded step(s) from trace ${traceId}: ${steps}. ` +
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
