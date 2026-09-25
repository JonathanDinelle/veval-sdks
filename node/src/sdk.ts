import { VevalOptions, ResolvedVevalOptions, resolveOptions } from "./options";
import { VevalExecutionContext } from "./context";
import { Step } from "./step";
import { TraceData, StepData, ReplayOptions, ReplayResult } from "./tracing";
import {
  SnapshotData,
  SnapshotDataHelper,
  SnapshotDiff,
  SnapshotComparer,
  SnapshotOptions,
  SnapshotPayloads,
} from "./snapshots";
import { ITraceAssertion, JudgeResult, JudgeOptions } from "./assertions";
import { ScenarioItem, ScenarioRunResult, ItemRunResult } from "./scenarios";
import { VevalHttpClient } from "./http-client";

export class VevalSdk {
  protected readonly opts: ResolvedVevalOptions;
  protected readonly http: VevalHttpClient;

  constructor(options: VevalOptions) {
    this.opts = resolveOptions(options);
    this.http = new VevalHttpClient(this.opts.apiKey, this.opts.endpoint);
  }

  async runAsync<T>(
    agentName: string,
    callback: (ctx: VevalExecutionContext) => Promise<T>,
    input?: unknown
  ): Promise<T> {
    const traceId = `tr_${crypto.randomUUID().replace(/-/g, "")}`;
    const ctx = new VevalExecutionContext(traceId, input ?? null);
    const startedAt = new Date();

    try {
      const result = await callback(ctx);
      const completedAt = new Date();
      const payload = VevalSdk.buildPayload(
        traceId, agentName, ctx,
        input ?? null, result, "success", null, startedAt, completedAt
      );
      await this.http.sendTraceAsync(payload);
      return result;
    } catch (err) {
      const completedAt = new Date();
      const error = err instanceof Error ? err.message : String(err);
      const payload = VevalSdk.buildPayload(
        traceId, agentName, ctx,
        input ?? null, null, "error", error, startedAt, completedAt
      );
      await this.http.sendTraceAsync(payload);
      throw err;
    }
  }

  async getTraceAsync(traceId: string): Promise<TraceData | null> {
    return this.http.getTraceAsync(traceId);
  }

  async judgeAsync(criteria: string, ctx: VevalExecutionContext, options?: JudgeOptions): Promise<JudgeResult> {
    const lastStep = ctx.steps[ctx.steps.length - 1];
    const payload = {
      criteria,
      input: lastStep ? lastStep.input : ctx.input,
      output: lastStep ? lastStep.output : null,
      model: options?.model ?? null,
      threshold: options?.threshold ?? null,
      reference_output: options?.referenceOutput ?? null,
      samples: options?.samples ?? null,
    };
    return this.http.judgeAsync(payload);
  }

  async loadSnapshotAsync(traceId: string): Promise<SnapshotData | null> {
    const trace = await this.http.getTraceAsync(traceId);
    return trace ? SnapshotDataHelper.fromTrace(trace) : null;
  }

  /**
   * Stores a named baseline — from a recorded trace ID (the trace is pinned so retention never deletes it),
   * or from a run you just executed. Saving again under the same name replaces the baseline.
   */
  async saveSnapshotAsync(snapshotName: string, source: string | VevalExecutionContext): Promise<SnapshotData> {
    const payload = typeof source === "string"
      ? SnapshotPayloads.saveFromTrace(snapshotName, source)
      : SnapshotPayloads.saveFromContext(snapshotName, source);
    return this.http.createSnapshotAsync(payload);
  }

  /** The latest stored baseline with this name, or null if none exists. */
  async getSnapshotAsync(snapshotName: string): Promise<SnapshotData | null> {
    return this.http.getSnapshotAsync(snapshotName);
  }

  /** Compares a run against a baseline and records the result in the dashboard. */
  async compareSnapshotAsync(
    snapshotName: string,
    snapshot: SnapshotData,
    ctx: VevalExecutionContext,
    options?: SnapshotOptions
  ): Promise<SnapshotDiff> {
    const diff = SnapshotComparer.compare(snapshot, ctx, options);
    await this.http.postScenarioRunAsync(snapshotName, SnapshotPayloads.run(snapshotName, diff));
    return diff;
  }

  async replayAsync<T>(
    trace: TraceData,
    callback: (ctx: VevalExecutionContext) => Promise<T>,
    options?: Partial<ReplayOptions>
  ): Promise<ReplayResult> {
    const ctx = new VevalExecutionContext(`tr_${crypto.randomUUID().replace(/-/g, "")}`, trace.input);
    if (options?.mock_llm_responses) {
      ctx.loadMockOutputs(trace);
    }

    const startedAt = new Date();
    let output: unknown = null;
    let status = "success";
    let error: string | null = null;

    try {
      output = await callback(ctx);
    } catch (err) {
      status = "error";
      error = err instanceof Error ? err.message : String(err);
    }

    const completedAt = new Date();
    const failures: string[] = [];
    if (error) failures.push(`Replay threw exception: ${error}`);
    for (const assertion of options?.assertions ?? []) {
      const failure = await assertion.evaluate(ctx);
      if (failure) failures.push(failure);
    }

    let recordingDiff: SnapshotDiff | null = null;
    if (options?.compare_with_recording) {
      recordingDiff = SnapshotComparer.compare(SnapshotDataHelper.fromTrace(trace), ctx, options.compare_with_recording);
      if (recordingDiff.has_changes)
        failures.push("Replay drifted from its recording — " + recordingDiff.summary(`recording ${trace.trace_id}`));
    }

    return {
      passed: failures.length === 0,
      failures,
      replayed_context: ctx,
      recording_diff: recordingDiff,
      output,
      started_at: startedAt.toISOString(),
      completed_at: completedAt.toISOString(),
      status,
      error,
    };
  }

  async runScenarioAsync<T>(
    scenarioName: string,
    agent: (ctx: VevalExecutionContext) => Promise<T>,
    scenarioAssertions: ITraceAssertion[],
    items?: ScenarioItem[]
  ): Promise<ScenarioRunResult> {
    const rawItems = items ?? (await this.http.getScenarioItemsAsync(scenarioName)) as ScenarioItem[];
    const results: ItemRunResult[] = [];

    for (const item of rawItems) {
      const effectiveAssertions = [...scenarioAssertions, ...(item.assertions ?? [])];
      const itemResult: ItemRunResult = { item, passed: true, failures: [] };

      if (item.trace_id) {
        const trace = await this.getTraceAsync(item.trace_id);
        if (!trace) {
          itemResult.failures.push(`trace '${item.trace_id}' not found`);
          itemResult.passed = false;
        } else {
          const replayResult = await this.replayAsync(trace, agent, {
            mock_llm_responses: true,
            assertions: effectiveAssertions,
          });
          itemResult.failures.push(...replayResult.failures);
          itemResult.passed = replayResult.passed;
          itemResult.context = replayResult.replayed_context ?? undefined;

          if (replayResult.replayed_context) {
            const replayCtx = replayResult.replayed_context;
            const payload = VevalSdk.buildPayload(
              replayCtx.traceId, scenarioName, replayCtx,
              trace.input, replayResult.output, replayResult.status, replayResult.error,
              new Date(replayResult.started_at), new Date(replayResult.completed_at),
              { replay: true, source_trace_id: item.trace_id }
            );
            await this.http.sendTraceAsync(payload);
          }
        }
      } else if (item.input !== undefined) {
        const ctx = await this._runAndCaptureContext(scenarioName, agent, item.input);
        for (const assertion of effectiveAssertions) {
          const failure = await assertion.evaluate(ctx);
          if (failure) {
            itemResult.failures.push(failure);
            itemResult.passed = false;
          }
        }
        itemResult.context = ctx;
      } else {
        itemResult.failures.push("ScenarioItem must have either trace_id or input");
        itemResult.passed = false;
      }

      results.push(itemResult);
    }

    const passCount = results.filter((r) => r.passed).length;
    const failCount = results.length - passCount;
    const scenarioResult: ScenarioRunResult = {
      passed: failCount === 0,
      pass_count: passCount,
      fail_count: failCount,
      results,
    };

    await this.http.postScenarioRunAsync(scenarioName, {
      passed: scenarioResult.passed,
      pass_count: scenarioResult.pass_count,
      fail_count: scenarioResult.fail_count,
      results: results.map((r) => ({
        name: r.item.name ?? r.item.trace_id ?? "synthetic",
        passed: r.passed,
        failures: r.failures,
        judgments: r.context?.judgments ?? [],
      })),
    });

    return scenarioResult;
  }

  private async _runAndCaptureContext<T>(
    agentName: string,
    agent: (ctx: VevalExecutionContext) => Promise<T>,
    input: unknown
  ): Promise<VevalExecutionContext> {
    const traceId = `tr_${crypto.randomUUID().replace(/-/g, "")}`;
    const ctx = new VevalExecutionContext(traceId, input);
    const startedAt = new Date();

    try {
      const result = await agent(ctx);
      const completedAt = new Date();
      const payload = VevalSdk.buildPayload(
        traceId, agentName, ctx,
        input, result, "success", null, startedAt, completedAt
      );
      await this.http.sendTraceAsync(payload);
    } catch (err) {
      const completedAt = new Date();
      const error = err instanceof Error ? err.message : String(err);
      const payload = VevalSdk.buildPayload(
        traceId, agentName, ctx,
        input, null, "error", error, startedAt, completedAt
      );
      await this.http.sendTraceAsync(payload);
    }

    return ctx;
  }

  static buildPayload(
    traceId: string,
    agentName: string,
    ctx: VevalExecutionContext,
    input: unknown,
    output: unknown,
    status: string,
    error: string | null,
    startedAt: Date,
    completedAt: Date,
    extraMeta?: Record<string, unknown>
  ): TraceData {
    const steps = VevalSdk.flattenSteps(ctx.steps);
    const totalCost = steps.reduce((sum, s) => sum + (s.costUsd ?? 0), 0);

    const metadata: Record<string, unknown> = { ...ctx.traceMeta, ...extraMeta };

    return {
      trace_id: traceId,
      agent_name: agentName,
      input,
      output,
      status,
      error,
      started_at: startedAt.toISOString(),
      completed_at: completedAt.toISOString(),
      duration_ms: completedAt.getTime() - startedAt.getTime(),
      total_cost_usd: totalCost || null,
      metadata,
      steps: VevalSdk.serializeSteps(ctx.steps),
    };
  }

  static flattenSteps(steps: readonly Step[]): Step[] {
    const result: Step[] = [];
    for (const s of steps) {
      result.push(s);
      result.push(...VevalSdk.flattenSteps(s.children));
    }
    return result;
  }

  private static serializeSteps(steps: readonly Step[]): StepData[] {
    const result: StepData[] = [];
    for (const s of steps) {
      result.push({
        step_id: s.stepId,
        parent_step_id: s.parentStepId,
        name: s.name,
        type: s.type,
        input: s.input,
        output: s.output,
        status: s.status,
        error: s.error,
        started_at: s.startedAt.toISOString(),
        completed_at: s.completedAt?.toISOString() ?? null,
        duration_ms: s.durationMs,
        cost_usd: s.costUsd,
        tokens_in: s.tokensIn,
        tokens_out: s.tokensOut,
        model: s.model,
        metadata: { ...s.metadata },
      });
      result.push(...VevalSdk.serializeSteps(s.children));
    }
    return result;
  }
}
