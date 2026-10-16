import { VevalOptions } from "./options";
import { VevalSdk, missingTraceMessage } from "./sdk";
import { VevalExecutionContext } from "./context";
import { TraceData, ReplayOptions } from "./tracing";
import { SnapshotData } from "./snapshots";
import { ITraceAssertion, JudgeResult } from "./assertions";
import { ScenarioItem, ScenarioRunResult, ItemRunResult } from "./scenarios";
import { ScenarioReport } from "./scenario-report";

export class VevalTestSdk extends VevalSdk {
  private _replayTrace: TraceData | null = null;
  private _lastStatus: string | null = null;
  private _lastError: string | null = null;
  private _judgeMocks: Map<string, JudgeResult[]> = new Map();
  private _snapshots: Map<string, SnapshotData> = new Map();

  constructor(options: VevalOptions) {
    super(options);
  }

  get lastStatus(): string | null {
    return this._lastStatus;
  }

  get lastError(): string | null {
    return this._lastError;
  }

  withReplay(trace: TraceData): this {
    this._replayTrace = trace;
    return this;
  }

  /**
   * Registers a canned judge result for the given criteria string, so scenarios/replays
   * using TraceAssert.judge stay deterministic and free in CI. Without a matching mock,
   * judgeAsync throws rather than silently making a real, billed LLM call.
   */
  withJudgeMock(criteria: string, passed: boolean, score = 1.0, reasoning = ""): this {
    const queue = this._judgeMocks.get(criteria) ?? [];
    queue.push({ score, passed, reasoning });
    this._judgeMocks.set(criteria, queue);
    return this;
  }

  override async judgeAsync(criteria: string): Promise<JudgeResult> {
    const queue = this._judgeMocks.get(criteria);
    const next = queue?.shift();
    if (next) return next;
    throw new Error(
      `Replay mode: no judge mock for criteria '${criteria}'. ` +
        "Call withJudgeMock(...) before running a scenario/replay that uses " +
        "TraceAssert.judge — this would have made a real, billed LLM call."
    );
  }

  override async runAsync<T>(
    agentName: string,
    callback: (ctx: VevalExecutionContext) => Promise<T>,
    input?: unknown
  ): Promise<T> {
    const traceId = `tr_${crypto.randomUUID().replace(/-/g, "")}`;
    const ctx = new VevalExecutionContext(traceId, input ?? null);
    if (this._replayTrace) {
      ctx.loadMockOutputs(this._replayTrace);
    }

    const startedAt = new Date();
    let output: T;
    try {
      output = await callback(ctx);
      // A recorded step the agent never called fails the run, the same way an unrecorded call does.
      const unused = this._replayTrace ? ctx.replayFidelity?.unusedFailure(this._replayTrace.trace_id) : null;
      if (unused) throw new Error(unused);
    } catch (err) {
      const error = err instanceof Error ? err.message : String(err);
      this._lastStatus = "error";
      this._lastError = error;
      // The agent's own error takes priority over a failed send (which is still logged).
      await this._sendRunTrace(ctx, agentName, input, null, "error", error, startedAt, false);
      throw err;
    }

    this._lastStatus = "success";
    this._lastError = null;
    await this._sendRunTrace(ctx, agentName, input, output, "success", null, startedAt);
    return output;
  }

  private async _sendRunTrace(
    ctx: VevalExecutionContext,
    agentName: string,
    input: unknown,
    output: unknown,
    status: string,
    error: string | null,
    startedAt: Date,
    throwOnFailure?: boolean
  ): Promise<void> {
    const extraMeta: Record<string, unknown> = { replay: true };
    if (this._replayTrace) extraMeta["source_trace_id"] = this._replayTrace.trace_id;
    const payload = VevalSdk.buildPayload(
      ctx.traceId, agentName, ctx,
      input ?? null, output, status, error, startedAt, new Date(), extraMeta
    );
    await this.http.sendTraceAsync(payload, throwOnFailure);
  }

  /**
   * Registers a baseline locally, so getSnapshotAsync / TraceAssert.matchesSnapshot work offline in CI.
   * Names without a local baseline are loaded from the server.
   */
  withSnapshot(snapshotName: string, snapshot: SnapshotData): this {
    this._snapshots.set(snapshotName, snapshot);
    return this;
  }

  override async getSnapshotAsync(snapshotName: string): Promise<SnapshotData | null> {
    return this._snapshots.get(snapshotName) ?? super.getSnapshotAsync(snapshotName);
  }

  override async getTraceAsync(traceId: string): Promise<TraceData | null> {
    if (this._replayTrace?.trace_id === traceId) return this._replayTrace;
    return null;
  }

  override async runScenarioAsync<T>(
    scenarioName: string,
    agent: (ctx: VevalExecutionContext) => Promise<T>,
    scenarioAssertions: ITraceAssertion[],
    items?: ScenarioItem[]
  ): Promise<ScenarioRunResult> {
    const resolvedItems = items ?? [];
    const results: ItemRunResult[] = [];
    const report = new ScenarioReport(this.http, scenarioName);

    for (const item of resolvedItems) {
      const effectiveAssertions = [...scenarioAssertions, ...(item.assertions ?? [])];
      const itemResult: ItemRunResult = { item, passed: true, failures: [] };

      if (item.trace_id) {
        const trace = await this.getTraceAsync(item.trace_id);
        if (!trace) {
          itemResult.failures.push(missingTraceMessage(item.trace_id));
          itemResult.passed = false;
        } else {
          const replayResult = await this.replayAsync(trace, agent, {
            mock_llm_responses: !!item.mock_model_steps,
            live_model_steps: !item.mock_model_steps,
            assertions: effectiveAssertions,
          } as ReplayOptions);
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
            await report.sendTraceAsync(payload);
          }
        }
      } else if (item.input !== undefined) {
        const ctx = new VevalExecutionContext(`tr_${crypto.randomUUID().replace(/-/g, "")}`, item.input);
        try {
          await agent(ctx);
        } catch (err) {
          const msg = err instanceof Error ? err.message : String(err);
          itemResult.failures.push(`Agent threw exception: ${msg}`);
          itemResult.passed = false;
        }
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

    return report.completeAsync(results, "item");
  }
}
