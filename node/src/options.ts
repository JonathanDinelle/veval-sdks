export interface VevalOptions {
  apiKey: string;
  projectId?: string;
  endpoint?: string;
  flushIntervalMs?: number;
  flushBatchSize?: number;
}

export function resolveOptions(opts: VevalOptions): Required<VevalOptions> {
  return {
    apiKey: opts.apiKey,
    projectId: opts.projectId ?? "",
    endpoint: opts.endpoint ?? "https://api.veval.dev",
    flushIntervalMs: opts.flushIntervalMs ?? 5000,
    flushBatchSize: opts.flushBatchSize ?? 50,
  };
}
