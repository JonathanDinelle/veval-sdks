import { Step } from "./step";
import { VevalExecutionContext } from "./context";

export interface ITraceAssertion {
  evaluate(ctx: VevalExecutionContext): Promise<string | null>;
}

export interface JudgeResult {
  score: number;
  passed: boolean;
  reasoning: string;
  threshold?: number;
}

export interface JudgeOptions {
  /** Must be in the provider's current model catalog, or the judge call fails. */
  model?: string;
  /** Pass/fail cutoff on the 0.0-1.0 score. Defaults server-side to 0.7 if omitted. */
  threshold?: number;
  /** An example of an output that fully satisfies the rubric, included in the grading prompt. */
  referenceOutput?: unknown;
  /**
   * Number of independent grading calls to average via median, for self-consistency.
   * Clamped server-side to [1, 5]. Each sample is billed, so higher values cost proportionally more.
   */
  samples?: number;
}

export interface JudgeableSdk {
  judgeAsync(criteria: string, ctx: VevalExecutionContext, options?: JudgeOptions): Promise<JudgeResult>;
}

function flattenSteps(steps: readonly Step[]): Step[] {
  const result: Step[] = [];
  for (const s of steps) {
    result.push(s);
    result.push(...flattenSteps(s.children));
  }
  return result;
}

export const TraceAssert = {
  maxSteps(max: number): ITraceAssertion {
    return {
      async evaluate(ctx) {
        const count = flattenSteps(ctx.steps).length;
        return count > max ? `MaxSteps: expected at most ${max} steps, got ${count}` : null;
      },
    };
  },

  noErrors(): ITraceAssertion {
    return {
      async evaluate(ctx) {
        const errors = flattenSteps(ctx.steps).filter((s) => s.status === "error");
        return errors.length > 0
          ? `NoErrors: found ${errors.length} step(s) with error status: ${errors.map((s) => s.name).join(", ")}`
          : null;
      },
    };
  },

  stepExists(stepName: string): ITraceAssertion {
    return {
      async evaluate(ctx) {
        const found = flattenSteps(ctx.steps).some((s) => s.name === stepName);
        return found ? null : `StepExists: step '${stepName}' not found`;
      },
    };
  },

  maxCost(maxCost: number): ITraceAssertion {
    return {
      async evaluate(ctx) {
        const total = flattenSteps(ctx.steps).reduce((sum, s) => sum + (s.costUsd ?? 0), 0);
        return total > maxCost ? `MaxCost: expected at most ${maxCost}, got ${total}` : null;
      },
    };
  },

  maxDuration(maxMs: number): ITraceAssertion {
    return {
      async evaluate(ctx) {
        const total = flattenSteps(ctx.steps).reduce((sum, s) => sum + (s.durationMs ?? 0), 0);
        return total > maxMs ? `MaxDuration: expected at most ${maxMs}ms, got ${total}ms` : null;
      },
    };
  },

  outputContains(expected: string): ITraceAssertion {
    return {
      async evaluate(ctx) {
        const found = flattenSteps(ctx.steps).some(
          (s) => s.output != null && String(s.output).includes(expected)
        );
        return found ? null : `OutputContains: no step output contains '${expected}'`;
      },
    };
  },

  toolCalled(toolName: string): ITraceAssertion {
    return {
      async evaluate(ctx) {
        const found = flattenSteps(ctx.steps).some(
          (s) => s.type === "tool" && s.name === toolName
        );
        return found ? null : `ToolCalled: no tool step named '${toolName}' was found`;
      },
    };
  },

  /**
   * Scores the trace's output against a rubric using an LLM judge, evaluated server-side.
   * The third argument accepts either a model name string (legacy shorthand) or a full
   * JudgeOptions object with threshold/referenceOutput/samples.
   */
  judge(veval: JudgeableSdk, criteria: string, modelOrOptions?: string | JudgeOptions): ITraceAssertion {
    const options: JudgeOptions | undefined =
      typeof modelOrOptions === "string" ? { model: modelOrOptions } : modelOrOptions;
    return {
      async evaluate(ctx) {
        let result: JudgeResult;
        try {
          result = await veval.judgeAsync(criteria, ctx, options);
        } catch (err) {
          // Never let a network/API failure during judging silently pass a test.
          const message = err instanceof Error ? err.message : String(err);
          return `Judge: evaluation failed — ${message}`;
        }

        ctx.recordJudgment(criteria, result.score, result.passed, result.reasoning);

        if (result.passed) return null;
        return `Judge: ${criteria} (score ${result.score.toFixed(2)}) — ${result.reasoning}`;
      },
    };
  },
};
