export interface VevalOptions {
  /** Your Veval API key — it also determines the workspace. */
  apiKey: string;
  /** @deprecated Ignored: the API key determines the workspace. Will be removed in a future version. */
  projectId?: string;
  flushIntervalMs?: number;
  flushBatchSize?: number;
}

export interface ResolvedVevalOptions {
  apiKey: string;
  /**
   * Veval's own hosted API, which enforces billing/quota (test-run limits, judge credits, etc.) — not a
   * self-hosted override for SDK consumers, so it isn't a public option. VEVAL_INTERNAL_ENDPOINT is an
   * undocumented escape hatch for Veval's own local development against a non-production API instance.
   */
  endpoint: string;
  flushIntervalMs: number;
  flushBatchSize: number;
}

export function resolveOptions(opts: VevalOptions): ResolvedVevalOptions {
  return {
    apiKey: opts.apiKey,
    endpoint: process.env.VEVAL_INTERNAL_ENDPOINT ?? "https://api.veval.dev",
    flushIntervalMs: opts.flushIntervalMs ?? 5000,
    flushBatchSize: opts.flushBatchSize ?? 50,
  };
}
