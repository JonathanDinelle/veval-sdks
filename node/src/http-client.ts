import { TraceData } from "./tracing";
import { JudgeResult } from "./assertions";

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
