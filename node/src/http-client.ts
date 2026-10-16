import { TraceData } from "./tracing";
import { JudgeResult } from "./assertions";
import type { SnapshotData } from "./snapshots";

/**
 * A trace or scenario run could not be delivered to Veval. Thrown only when the throwOnSendFailure
 * option is set; otherwise the failure is logged as a warning.
 */
export class VevalSendError extends Error {
  constructor(message: string, readonly status: number | null = null) {
    super(message);
    this.name = "VevalSendError";
  }
}

/**
 * Loading something the SDK needs from Veval failed, such as a scenario's items or a trace to replay. Thrown
 * whatever throwOnSendFailure says, since the caller can't continue without it.
 */
export class VevalRequestError extends Error {
  constructor(message: string, readonly status: number | null = null) {
    super(message);
    this.name = "VevalRequestError";
  }
}

export class VevalHttpClient {
  private readonly endpoint: string;
  private readonly apiKey: string;

  readonly throwOnSendFailure: boolean;

  constructor(apiKey: string, endpoint: string, throwOnSendFailure = false) {
    this.apiKey = apiKey;
    this.endpoint = endpoint.replace(/\/$/, "");
    this.throwOnSendFailure = throwOnSendFailure;
  }

  private get headers(): Record<string, string> {
    return {
      "Content-Type": "application/json",
      Authorization: `Bearer ${this.apiKey}`,
    };
  }

  /**
   * Never throws unless throwOnFailure (default: the throwOnSendFailure option) is set; a failed send is
   * logged as a warning instead, so tracing can't break the host app. Resolves to whether the trace was stored.
   */
  async sendTraceAsync(payload: unknown, throwOnFailure?: boolean): Promise<boolean> {
    return this.sendAsync("Sending trace", `${this.endpoint}/v1/traces`, payload, throwOnFailure);
  }

  /**
   * Resolves to null only when the trace doesn't exist (404); any other failure throws VevalRequestError, so
   * a wrong API key or a network error can't look like a missing trace.
   */
  async getTraceAsync(traceId: string): Promise<TraceData | null> {
    return this.getJsonAsync<TraceData>(`Loading trace '${traceId}'`, `${this.endpoint}/v1/traces/${traceId}`, true);
  }

  /**
   * Throws VevalRequestError on failure — returning an empty list would run zero items and report the
   * scenario as passed.
   */
  async getScenarioItemsAsync(scenarioName: string): Promise<unknown[]> {
    const body = await this.getJsonAsync<{ items?: unknown[] }>(
      `Loading items for scenario '${scenarioName}'`,
      `${this.endpoint}/v1/scenarios/${encodeURIComponent(scenarioName)}/items`
    );
    return body?.items ?? [];
  }

  private async getJsonAsync<T>(what: string, url: string, nullOn404 = false): Promise<T | null> {
    let res: Response;
    try {
      res = await fetch(url, { headers: this.headers });
    } catch (err) {
      throw new VevalRequestError(`Veval: ${what} failed: ${networkFailureReason(err)}`);
    }
    if (nullOn404 && res.status === 404) {
      await res.body?.cancel();
      return null;
    }
    if (!res.ok) {
      throw new VevalRequestError(`Veval: ${what} failed with status ${res.status}${await reason(res)}`, res.status);
    }
    return (await res.json()) as T;
  }

  /** Same failure behaviour as sendTraceAsync. */
  async postScenarioRunAsync(scenarioName: string, payload: unknown): Promise<void> {
    await this.sendAsync(
      `Posting scenario run '${scenarioName}'`,
      `${this.endpoint}/v1/scenarios/${encodeURIComponent(scenarioName)}/runs`,
      payload
    );
  }

  private async sendAsync(what: string, url: string, payload: unknown, throwOnFailure?: boolean): Promise<boolean> {
    let error: VevalSendError;
    try {
      const res = await fetch(url, { method: "POST", headers: this.headers, body: JSON.stringify(payload) });
      if (res.ok) {
        // Release the connection now rather than when the response is garbage-collected.
        await res.body?.cancel();
        return true;
      }
      error = new VevalSendError(`Veval: ${what} failed with status ${res.status}${await reason(res)}`, res.status);
    } catch (err) {
      error = new VevalSendError(`Veval: ${what} failed: ${networkFailureReason(err)}`);
    }

    console.warn(error.message);
    if (throwOnFailure ?? this.throwOnSendFailure) throw error;
    return false;
  }

  /**
   * Saves a named snapshot baseline. Throws on failure — a baseline that silently didn't save would
   * make every later comparison fail, or compare against a stale one.
   */
  async createSnapshotAsync(payload: unknown): Promise<SnapshotData> {
    const res = await fetch(`${this.endpoint}/v1/snapshots`, {
      method: "POST",
      headers: this.headers,
      body: JSON.stringify(payload),
    });
    if (!res.ok) {
      throw new Error(`Saving snapshot failed with status ${res.status}: ${await res.text()}`);
    }
    return (await res.json()) as SnapshotData;
  }

  /**
   * Loads the latest baseline with this name. Returns null only when none exists (404); any other
   * failure throws, so a network error can never look like "nothing to compare against".
   */
  async getSnapshotAsync(name: string): Promise<SnapshotData | null> {
    const res = await fetch(`${this.endpoint}/v1/snapshots/${encodeURIComponent(name)}`, {
      headers: this.headers,
    });
    if (res.status === 404) {
      await res.body?.cancel();
      return null;
    }
    if (!res.ok) {
      throw new Error(`Loading snapshot '${name}' failed with status ${res.status}: ${await res.text()}`);
    }
    return (await res.json()) as SnapshotData;
  }

  // Unlike the other calls on this client, judgeAsync deliberately does not swallow
  // failures — a network/API error here must surface as an assertion failure, not a
  // silent pass. Callers are expected to catch.
  async judgeAsync(payload: unknown): Promise<JudgeResult> {
    const res = await fetch(`${this.endpoint}/v1/judge`, {
      method: "POST",
      headers: this.headers,
      body: JSON.stringify(payload),
    });
    if (!res.ok) {
      throw new Error(`Judge request failed with status ${res.status}: ${await res.text()}`);
    }
    return (await res.json()) as JudgeResult;
  }
}

/** The response body as ": body", cut to 500 characters, or "" when it's empty. */
async function reason(res: Response): Promise<string> {
  const body = (await res.text()).slice(0, 500);
  return body ? `: ${body}` : "";
}

/**
 * fetch reports a network failure as "fetch failed" with the real reason in cause. On Node 20+ a refused
 * connection's cause is an AggregateError with an empty message, so fall back to its code (ECONNREFUSED).
 */
function networkFailureReason(err: unknown): string {
  const cause = (err as { cause?: unknown }).cause as { message?: string; code?: string } | undefined;
  const outer = err instanceof Error ? err.message : String(err);
  return cause?.message || cause?.code || outer;
}
