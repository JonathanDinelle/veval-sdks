import { TraceData } from "./tracing";
import { JudgeResult } from "./assertions";
import type { SnapshotData } from "./snapshots";

export class VevalHttpClient {
  private readonly endpoint: string;
  private readonly apiKey: string;

  constructor(apiKey: string, endpoint: string) {
    this.apiKey = apiKey;
    this.endpoint = endpoint.replace(/\/$/, "");
  }

  private get headers(): Record<string, string> {
    return {
      "Content-Type": "application/json",
      Authorization: `Bearer ${this.apiKey}`,
    };
  }

  async sendTraceAsync(payload: unknown): Promise<void> {
    try {
      await fetch(`${this.endpoint}/v1/traces`, {
        method: "POST",
        headers: this.headers,
        body: JSON.stringify(payload),
      });
    } catch {
      // swallow silently
    }
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

  async postScenarioRunAsync(scenarioName: string, payload: unknown): Promise<void> {
    try {
      await fetch(
        `${this.endpoint}/v1/scenarios/${encodeURIComponent(scenarioName)}/runs`,
        {
          method: "POST",
          headers: this.headers,
          body: JSON.stringify(payload),
        }
      );
    } catch {
      // swallow silently
    }
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
