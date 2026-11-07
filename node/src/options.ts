export interface VevalOptions {
  /** Your Veval API key — it also determines the workspace. */
  apiKey: string;
  /** @deprecated Ignored: the API key determines the workspace. Will be removed in a future version. */
  projectId?: string;
  /**
   * By default a trace or scenario run that can't be delivered (bad API key, quota, network) is logged
   * with console.warn and never breaks the host app. Set this in tests and CI to throw VevalSendError instead.
   */
  throwOnSendFailure?: boolean;
}

export interface ResolvedVevalOptions {
  apiKey: string;
  /**
   * Veval's own hosted API, which enforces billing/quota (test-run limits, judge credits, etc.) — not a
   * self-hosted override for SDK consumers, so it isn't a public option. VEVAL_INTERNAL_ENDPOINT is an
   * undocumented escape hatch for Veval's own local development against a non-production API instance.
   */
  endpoint: string;
  throwOnSendFailure: boolean;
}

export function resolveOptions(opts: VevalOptions): ResolvedVevalOptions {
  return {
    apiKey: opts.apiKey,
    endpoint: process.env.VEVAL_INTERNAL_ENDPOINT ?? "https://api.veval.dev",
    throwOnSendFailure: opts.throwOnSendFailure ?? false,
  };
}
