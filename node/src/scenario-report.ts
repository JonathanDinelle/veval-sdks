import { VevalHttpClient, VevalSendError } from "./http-client";
import { ItemRunResult, ScenarioRunResult } from "./scenarios";
import { replaySummary, scenarioSummary } from "./summary";

/**
 * Reports one scenario run. Item traces are sent without throwing, so one failed send can't abort the
 * remaining items; each failure is counted, and in strict mode completeAsync throws once, after the run is posted.
 */
export class ScenarioReport {
  private undelivered = 0;

  constructor(private readonly http: VevalHttpClient, private readonly scenarioName: string) {}

  async sendTraceAsync(payload: unknown): Promise<void> {
    if (!(await this.http.sendTraceAsync(payload, false))) this.undelivered++;
  }

  /**
   * Posts the run for trend tracking and resolves to its result. Throws only in strict mode (throwOnSendFailure).
   * `unnamedItem` is the name posted for an item with neither a name nor a trace_id.
   */
  async completeAsync(results: ItemRunResult[], unnamedItem: string, maxInconclusiveRate = 0): Promise<ScenarioRunResult> {
    const scenarioResult = ScenarioReport.result(results, maxInconclusiveRate);
    await this.http.postScenarioRunAsync(this.scenarioName, ScenarioReport.runPayload(scenarioResult, unnamedItem));

    if (this.undelivered > 0 && this.http.throwOnSendFailure) {
      throw new VevalSendError(
        `Veval: ${this.undelivered} trace(s) from scenario '${this.scenarioName}' could not be stored; see the warnings above`
      );
    }
    return scenarioResult;
  }

  /** @internal The run's result: counts, the inconclusive threshold, and each item's outcome and summary. */
  static result(results: ItemRunResult[], maxInconclusiveRate = 0): ScenarioRunResult {
    for (const r of results) {
      r.outcome = r.inconclusive ? "inconclusive" : r.passed ? "passed" : "failed";
      r.replayed = !!r.fidelity || !!r.inconclusive;
      r.summary = replaySummary(r.failures, r.inconclusive, r.fidelity);
      r.faults = r.fidelity?.faults ?? [];
      r.inconclusive_after_fault = r.inconclusive?.reason === "after_fault";
    }
    const passCount = results.filter((r) => r.outcome === "passed").length;
    const inconclusiveCount = results.filter((r) => r.outcome === "inconclusive").length;
    const afterFault = results.filter((r) => r.inconclusive_after_fault).length;
    const failCount = results.length - passCount - inconclusiveCount;
    // Only a replay can be inconclusive, so synthetic items don't dilute the rate. Items stopped by a fault are counted
    // on their own: the rate measures how far live replay stays on its recording.
    const replayed = results.filter((r) => r.replayed && !r.inconclusive_after_fault).length;
    const otherInconclusive = inconclusiveCount - afterFault;
    const stayedOnPath = replayed - otherInconclusive;
    const inconclusiveRate = replayed === 0 ? 0 : otherInconclusive / replayed;
    const counts = {
      passed: failCount === 0 && afterFault === 0 && inconclusiveRate <= maxInconclusiveRate,
      pass_count: passCount,
      fail_count: failCount,
      inconclusive_count: inconclusiveCount,
      inconclusive_after_fault_count: afterFault,
      inconclusive_rate: inconclusiveRate,
      max_inconclusive_rate: maxInconclusiveRate,
    };
    return {
      ...counts,
      stayed_on_path_rate: replayed === 0 ? null : stayedOnPath / replayed,
      summary: scenarioSummary({ ...counts, replayed, stayed_on_path: stayedOnPath }),
      results,
    };
  }

  /** @internal The run as posted: a third item outcome, and the stop of each inconclusive item. */
  static runPayload(result: ScenarioRunResult, unnamedItem: string): unknown {
    return {
      passed: result.passed,
      pass_count: result.pass_count,
      fail_count: result.fail_count,
      inconclusive_count: result.inconclusive_count,
      inconclusive_after_fault_count: result.inconclusive_after_fault_count,
      max_inconclusive_rate: result.max_inconclusive_rate,
      summary: result.summary,
      results: result.results.map((r) => ({
        name: r.item.name ?? r.item.trace_id ?? unnamedItem,
        // Kept for readers that predate outcome: true only when the item passed.
        passed: r.outcome === "passed",
        outcome: r.outcome,
        replayed: !!r.replayed,
        inconclusive: r.inconclusive
          ? {
              step: r.inconclusive.step,
              step_name: r.inconclusive.step_name,
              reason: r.inconclusive.reason,
              recorded_input: r.inconclusive.recorded_input ?? null,
              actual_input: r.inconclusive.actual_input ?? null,
              message: r.inconclusive.message,
            }
          : null,
        summary: r.summary,
        // Each injected fault and how many times it fired, so the run shows which items tested a failure path.
        faults: (r.faults ?? []).map((f) => ({ description: f.fault.description, step_name: f.fault.step_name, fired: f.fired })),
        failures: r.failures,
        judgments: r.context?.judgments ?? [],
      })),
    };
  }
}
