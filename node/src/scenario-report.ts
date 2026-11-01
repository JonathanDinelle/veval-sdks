import { VevalHttpClient, VevalSendError } from "./http-client";
import { ItemRunResult, ScenarioRunResult } from "./scenarios";

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
  async completeAsync(results: ItemRunResult[], unnamedItem: string): Promise<ScenarioRunResult> {
    const passCount = results.filter((r) => r.passed).length;
    const inconclusiveCount = results.filter((r) => r.inconclusive).length;
    const failCount = results.length - passCount - inconclusiveCount;
    const scenarioResult: ScenarioRunResult = {
      passed: passCount === results.length,
      pass_count: passCount,
      fail_count: failCount,
      inconclusive_count: inconclusiveCount,
      results,
    };

    await this.http.postScenarioRunAsync(this.scenarioName, {
      passed: scenarioResult.passed,
      pass_count: scenarioResult.pass_count,
      // The dashboard has no inconclusive state yet (VEV-39): an inconclusive item is posted as not passed,
      // with its stop as the reason.
      fail_count: failCount + inconclusiveCount,
      results: results.map((r) => ({
        name: r.item.name ?? r.item.trace_id ?? unnamedItem,
        passed: r.passed,
        failures: r.inconclusive ? [r.inconclusive.message] : r.failures,
        judgments: r.context?.judgments ?? [],
      })),
    });

    if (this.undelivered > 0 && this.http.throwOnSendFailure) {
      throw new VevalSendError(
        `Veval: ${this.undelivered} trace(s) from scenario '${this.scenarioName}' could not be stored; see the warnings above`
      );
    }
    return scenarioResult;
  }
}
