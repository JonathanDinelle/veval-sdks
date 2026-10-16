/** How a replayed call was bound to the recording. */
export type ReplayBindingKind =
  /** Served the recorded step with the same name and an identical input. */
  | "matched"
  /** No recorded step with that name had an identical input; served the next unused one in recorded order. */
  | "diverged";

/** One replayed call and the recorded step that served it. */
export interface ReplayBinding {
  step_name: string;
  kind: ReplayBindingKind;
  recorded_index: number;
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

  get is_faithful(): boolean {
    return this.diverged === 0 && this._unused.length === 0;
  }

  /** @internal */
  record(binding: ReplayBinding): void {
    this._bindings.push(binding);
    this._unused = this._unused.filter((u) => u.recorded_index !== binding.recorded_index);
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
}
