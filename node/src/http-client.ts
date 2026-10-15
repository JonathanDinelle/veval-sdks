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

export class VevalHttpClient {
  private readonly endpoint: string;
  private readonly apiKey: string;
  private readonly throwOnSendFailure: boolean;

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
   * logged as a warning instead, so tracing can't break the host app.
   */
  async sendTraceAsync(payload: unknown, throwOnFailure?: boolean): Promise<void> {
    await this.sendAsync("Sending trace", `${this.endpoint}/v1/traces`, payload, throwOnFailure);
  }

  async getTraceAsync(traceId: string): Promise<TraceData | null> {
    try {
      const res = await fetch(`${this.endpoint}/v1/traces/${traceId}`, {
        headers: this.headers,
      });
      if (!res.ok) return null;
      return (await res.json()) as TraceData;
    } catch {
      return null;
    }
  }

  async getScenarioItemsAsync(scenarioName: string): Promise<unknown[]> {
    try {
      const res = await fetch(
        `${this.endpoint}/v1/scenarios/${encodeURIComponent(scenarioName)}/items`,
        { headers: this.headers }
      );
      if (!res.ok) return [];
      const body = (await res.json()) as { items: unknown[] };
      return body.items ?? [];
    } catch {
      return [];
    }
  }

  /** Same failure behaviour as sendTraceAsync. */
  async postScenarioRunAsync(scenarioName: string, payload: unknown): Promise<void> {
    await this.sendAsync(
      `Posting scenario run '${scenarioName}'`,
      `${this.endpoint}/v1/scenarios/${encodeURIComponent(scenarioName)}/runs`,
      payload
    );
  }

  private async sendAsync(what: string, url: string, payload: unknown, throwOnFailure?: boolean): Promise<void> {
    let error: VevalSendError;
    try {
      const res = await fetch(url, { method: "POST", headers: this.headers, body: JSON.stringify(payload) });
      if (res.ok) return;
      const body = (await res.text()).slice(0, 500);
      const reason = body ? `: ${body}` : "";
      error = new VevalSendError(`Veval: ${what} failed with status ${res.status}${reason}`, res.status);
    } catch (err) {
      // fetch reports a network failure as "fetch failed" with the real reason in cause.
      const cause = (err as { cause?: unknown }).cause;
      const reason = cause instanceof Error ? cause.message : err instanceof Error ? err.message : String(err);
      error = new VevalSendError(`Veval: ${what} failed: ${reason}`);
    }

    console.warn(error.message);
    if (throwOnFailure ?? this.throwOnSendFailure) throw error;
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
    if (res.status === 404) return null;
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
