export class Step {
  readonly stepId: string = crypto.randomUUID().replace(/-/g, "");
  readonly parentStepId: string | null;
  readonly name: string;
  type: string = "custom";
  input: unknown = null;
  output: unknown = null;
  /** "inconclusive": a live replay stopped at this step (`ReplayStop`), so it neither succeeded nor failed. */
  status: "running" | "success" | "error" | "inconclusive" = "running";
  error: string | null = null;
  readonly startedAt: Date = new Date();
  completedAt: Date | null = null;
  durationMs: number | null = null;
  costUsd: number | null = null;
  tokensIn: number | null = null;
  tokensOut: number | null = null;
  model: string | null = null;
  readonly metadata: Record<string, unknown> = {};
  private _children: Step[] = [];

  constructor(name: string, parentStepId: string | null = null) {
    this.name = name;
    this.parentStepId = parentStepId;
  }

  get children(): readonly Step[] {
    return this._children;
  }

  complete(output: unknown): void {
    this.output = output;
    this.status = "success";
    this.completedAt = new Date();
    this.durationMs = this.completedAt.getTime() - this.startedAt.getTime();
  }

  fail(error: string): void {
    this.error = error;
    this.status = "error";
    this.completedAt = new Date();
    this.durationMs = this.completedAt.getTime() - this.startedAt.getTime();
  }

  /** @internal A live replay stopped at this step: neither a success nor an error. */
  stopped(message: string): void {
    this.error = message;
    this.status = "inconclusive";
    this.completedAt = new Date();
    this.durationMs = this.completedAt.getTime() - this.startedAt.getTime();
  }

  createStep(name: string): Step {
    const child = new Step(name, this.stepId);
    this._children.push(child);
    return child;
  }

  setMetadata(key: string, value: unknown): void {
    this.metadata[key] = value;
  }
}

export class StepHandle {
  constructor(private readonly step: Step) {}

  setMeta(key: string, value: unknown): void {
    switch (key) {
      case "tokens_in":
        this.step.tokensIn = value as number;
        break;
      case "tokens_out":
        this.step.tokensOut = value as number;
        break;
      case "cost_usd":
        this.step.costUsd = value as number;
        break;
      case "model":
        this.step.model = value as string;
        break;
      case "type":
        this.step.type = value as string;
        break;
      default:
        this.step.setMetadata(key, value);
    }
  }
}

/** Step types Veval knows. `Step.type` is a string, so these are its usual values. */
export const StepType = {
  /** A model call. Live replay runs it for real. */
  Llm: "llm",
  /** A tool call. Replay serves it from the recording, and a live replay stops when its input diverges. */
  Tool: "tool",
  /** Anything else; the default. */
  Custom: "custom",
} as const;

/** Settings for one step, given where the step is called. */
export interface StepOptions {
  /**
   * The step's type (`StepType`), known before the step runs. A live replay uses it when the recording doesn't know
   * the step's type: a step it never contained, or one saved untyped (`"custom"`). A model step (`"llm"`) then runs
   * live instead of stopping the replay or being served its old output. A recorded `"llm"` or `"tool"` type wins, and
   * a different type here is reported on the binding (`declared_type`), so a mislabelled call can't turn a recorded
   * tool step live. New recordings carry this type; `setMeta("type", …)` inside the step still overrides it.
   */
  type?: string;
}
