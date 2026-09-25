import { StepData, VevalExecutionContext, VevalSdk, VevalTestSdk } from "../src";

// Point every SDK at a closed local port: reporting calls fail fast and are swallowed, and nothing
// reaches the real API. Set before any SDK is constructed (the endpoint is read at construction).
process.env.VEVAL_INTERNAL_ENDPOINT = "http://127.0.0.1:1";

export const sdk = () => new VevalSdk({ apiKey: "test" });
export const testSdk = () => new VevalTestSdk({ apiKey: "test" });

/** A context with the given (name, input, output) steps already tracked. */
export async function run(...steps: Array<[string, unknown, unknown]>): Promise<VevalExecutionContext> {
  const ctx = new VevalExecutionContext("tr_run", null);
  for (const [name, input, output] of steps) await ctx.trackStepAsync(name, input, async () => output);
  return ctx;
}

export const recorded = (name: string, input: unknown, output: unknown, type = "llm"): StepData => ({
  step_id: name, parent_step_id: null, name, type, input, output, status: "success", error: null,
  started_at: new Date().toISOString(), completed_at: null, duration_ms: null, cost_usd: null,
  tokens_in: null, tokens_out: null, model: null, metadata: {},
});
