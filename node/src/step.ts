export class Step {
  readonly stepId: string = crypto.randomUUID().replace(/-/g, "");
  readonly parentStepId: string | null;
  readonly name: string;
  type: string = "custom";
  input: unknown = null;
  output: unknown = null;
  status: "running" | "success" | "error" = "running";
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
