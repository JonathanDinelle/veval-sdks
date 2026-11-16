/** What a `ReplayFault` serves in place of a step's recorded output. */
export type FaultKind =
  /** Throws a `FaultTimeoutError` (`name === "TimeoutError"`, as `AbortSignal.timeout` reports it). */
  | "timeout"
  /** Throws a `FaultHttpError` with the fault's status. */
  | "http_error"
  /** Throws a `FaultHttpError` with status 429 and a `Retry-After` header. */
  | "rate_limit"
  /** Returns a malformed value instead of throwing (`MalformedOutput`). */
  | "malformed"
  /** Returns a value you give instead of the recorded output (`ReplayFault.returns`). */
  | "returns"
  /** Throws the error the fault's factory builds. */
  | "custom";

/**
 * A malformed output a `ReplayFault.malformed` fault returns.
 * - `truncated`: the step's recorded output as JSON text, cut in half. Needs an unused recording of the step.
 * - `empty`: `""` for a step whose output is text (the recording holds text), `null` otherwise.
 */
export type MalformedOutput = "truncated" | "empty";

/**
 * Serves a declared failure instead of a step's recorded output, so a replay exercises the retries, fallbacks and
 * guards a recording almost never contains. Add faults to `ReplayOptions.faults`. Deterministic: a fault fires on the
 * calls it targets and nowhere else.
 *
 * By default a fault fires on every call of its step. `times(n)` limits it to the first n matching calls, after which
 * the step is served from the recording again, which tests recovery. A faulted call never uses up a recording, so a
 * retry with the same input gets the recorded output. A fault that never fires fails the replay.
 */
export class ReplayFault {
  readonly step_name: string;
  readonly kind: FaultKind;
  /** The HTTP status of an `http_error` or `rate_limit` fault. */
  readonly status: number | null;
  /** The `Retry-After` of a `rate_limit` fault, in seconds. */
  readonly retry_after_seconds: number | null;
  /** What a `malformed` fault returns. */
  readonly malformed_output: MalformedOutput | null;
  /** The value a `returns` fault returns. */
  readonly value: unknown;
  private readonly _errorFactory: (() => unknown) | null;
  private _count: number | null = null;
  private _fromCall = 1;
  private _hasInput = false;
  private _input: unknown = undefined;

  private constructor(
    stepName: string,
    kind: FaultKind,
    extra: {
      status?: number; retryAfterSeconds?: number; malformed?: MalformedOutput; value?: unknown;
      factory?: () => unknown;
    } = {}
  ) {
    if (!stepName) throw new Error("A fault needs a step name.");
    this.step_name = stepName;
    this.kind = kind;
    this.status = extra.status ?? null;
    this.retry_after_seconds = extra.retryAfterSeconds ?? null;
    this.malformed_output = extra.malformed ?? null;
    this.value = extra.value;
    this._errorFactory = extra.factory ?? null;
  }

  /** The step times out: a `FaultTimeoutError`, whose `name` is `"TimeoutError"`. */
  static timeout(stepName: string): ReplayFault {
    return new ReplayFault(stepName, "timeout");
  }

  /** The step fails with an HTTP error status, e.g. 500 or 503 (`FaultHttpError`). */
  static httpError(stepName: string, status: number): ReplayFault {
    if (!Number.isInteger(status) || status < 400 || status > 599)
      throw new RangeError(`An HTTP error status is between 400 and 599, got ${status}.`);
    return new ReplayFault(stepName, "http_error", { status });
  }

  /** The step is rate limited: status 429 with a `Retry-After` (one second unless given). */
  static rateLimit(stepName: string, retryAfterSeconds = 1): ReplayFault {
    if (!(retryAfterSeconds >= 0)) throw new RangeError("Retry-After can't be negative.");
    return new ReplayFault(stepName, "rate_limit", { status: 429, retryAfterSeconds });
  }

  /** The step returns a malformed output instead of throwing, so the guard that parses it runs. */
  static malformed(stepName: string, output: MalformedOutput): ReplayFault {
    if (output !== "truncated" && output !== "empty")
      throw new RangeError(`A malformed output is "truncated" or "empty", not ${JSON.stringify(output)}. Use ReplayFault.returns for a value.`);
    return new ReplayFault(stepName, "malformed", { malformed: output });
  }

  /**
   * The step returns this value instead of its recorded output: a value of the wrong type, or the value your step
   * returns when its own call fails (an error message, a fallback), to test the code that reads it.
   */
  static returns(stepName: string, value: unknown): ReplayFault {
    return new ReplayFault(stepName, "returns", { value });
  }

  /** The step throws the error this factory builds, for clients with their own error types. */
  static throw(stepName: string, errorFactory: () => unknown): ReplayFault {
    if (typeof errorFactory !== "function") throw new TypeError("ReplayFault.throw needs an error factory.");
    return new ReplayFault(stepName, "custom", { factory: errorFactory });
  }

  /** How many matching calls the fault fails; null fails every one. */
  get count(): number | null {
    return this._count;
  }

  /** The 1-based call of the step from which the fault applies; earlier calls are served as usual. */
  get from_call(): number {
    return this._fromCall;
  }

  /** True when the fault only applies to calls whose input equals `input`. */
  get has_input(): boolean {
    return this._hasInput;
  }

  get input(): unknown {
    return this._input;
  }

  /** Fail only the first `count` matching calls, then serve the recording again. */
  times(count: number): this {
    if (!Number.isInteger(count) || count < 1) throw new RangeError("A fault fires at least once.");
    this._count = count;
    return this;
  }

  /** Apply from the `call`-th call of the step (1-based, retries included). */
  onCall(call: number): this {
    if (!Number.isInteger(call) || call < 1) throw new RangeError("Calls are numbered from 1.");
    this._fromCall = call;
    return this;
  }

  /**
   * Apply only to calls whose input equals this one, compared as replay matches recordings (key order and the step's
   * `ignore_input_fields` aside).
   */
  whenInput(input: unknown): this {
    this._input = input;
    this._hasInput = true;
    return this;
  }

  /** A short label, e.g. `Timeout on 'search_flights'` or `HTTP 503 on 'book_hotel' (first 2 calls)`. */
  get description(): string {
    const kind =
      this.kind === "timeout" ? "Timeout"
      : this.kind === "http_error" ? `HTTP ${this.status}`
      : this.kind === "rate_limit" ? `Rate limit (429, Retry-After ${formatSeconds(this.retry_after_seconds!)}s)`
      : this.kind === "malformed" ? `Malformed output (${this.malformed_output})`
      : this.kind === "returns" ? "Returned value"
      : "Custom exception";
    const scope: string[] = [];
    if (this._count !== null) scope.push(this._count === 1 ? "first call" : `first ${this._count} calls`);
    if (this._fromCall > 1) scope.push(`from call ${this._fromCall}`);
    if (this._hasInput) scope.push("matching input");
    return `${kind} on '${this.step_name}'` + (scope.length > 0 ? ` (${scope.join(", ")})` : "");
  }

  toString(): string {
    return this.description;
  }

  /** @internal The error a throwing fault raises at a call of its step. */
  createError(): unknown {
    const message = `Injected fault: ${this.description}.`;
    switch (this.kind) {
      case "timeout":
        return new FaultTimeoutError(message);
      case "http_error":
      case "rate_limit":
        return new FaultHttpError(message, this.status!, this.retry_after_seconds);
      case "custom": {
        const error = this._errorFactory!();
        if (error === null || error === undefined)
          throw new Error(`The error factory of the fault on '${this.step_name}' returned nothing.`);
        return error;
      }
      default:
        throw new Error(`A ${this.kind} fault returns a value; it doesn't throw.`);
    }
  }
}

function formatSeconds(seconds: number): string {
  return String(Math.round(seconds * 1000) / 1000);
}

/** The timeout a `ReplayFault.timeout` fault throws. Its `name` is `"TimeoutError"`, as `AbortSignal.timeout` reports it. */
export class FaultTimeoutError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "TimeoutError";
  }
}

/**
 * The HTTP failure a `ReplayFault.httpError` or `ReplayFault.rateLimit` fault throws. `status` and `headers` carry
 * what the response would have, as HTTP client errors usually do; a rate limit sets `headers["retry-after"]`.
 */
export class FaultHttpError extends Error {
  readonly status: number;
  /** The 429's `Retry-After` in seconds, or null. */
  readonly retry_after_seconds: number | null;
  /** Lowercase response headers: `retry-after` (in whole seconds) for a rate limit. */
  readonly headers: Record<string, string>;

  constructor(message: string, status: number, retryAfterSeconds: number | null = null) {
    super(message);
    this.name = "FaultHttpError";
    this.status = status;
    this.retry_after_seconds = retryAfterSeconds;
    this.headers = retryAfterSeconds !== null ? { "retry-after": String(Math.ceil(retryAfterSeconds)) } : {};
  }
}

/** How many times one fault fired during a replay. */
export interface FaultReport {
  fault: ReplayFault;
  fired: number;
  triggered: boolean;
}
