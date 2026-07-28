import { Step } from "./step";
import { VevalExecutionContext } from "./context";

export interface ITraceAssertion {
  evaluate(ctx: VevalExecutionContext): string | null;
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
      evaluate(ctx) {
        const count = flattenSteps(ctx.steps).length;
        return count > max ? `MaxSteps: expected at most ${max} steps, got ${count}` : null;
      },
    };
  },

  noErrors(): ITraceAssertion {
    return {
      evaluate(ctx) {
        const errors = flattenSteps(ctx.steps).filter((s) => s.status === "error");
        return errors.length > 0
          ? `NoErrors: found ${errors.length} step(s) with error status: ${errors.map((s) => s.name).join(", ")}`
          : null;
      },
    };
  },

  stepExists(stepName: string): ITraceAssertion {
    return {
      evaluate(ctx) {
        const found = flattenSteps(ctx.steps).some((s) => s.name === stepName);
        return found ? null : `StepExists: step '${stepName}' not found`;
      },
    };
  },

  maxCost(maxCost: number): ITraceAssertion {
    return {
      evaluate(ctx) {
        const total = flattenSteps(ctx.steps).reduce((sum, s) => sum + (s.costUsd ?? 0), 0);
        return total > maxCost ? `MaxCost: expected at most ${maxCost}, got ${total}` : null;
      },
    };
  },

  maxDuration(maxMs: number): ITraceAssertion {
    return {
      evaluate(ctx) {
        const total = flattenSteps(ctx.steps).reduce((sum, s) => sum + (s.durationMs ?? 0), 0);
        return total > maxMs ? `MaxDuration: expected at most ${maxMs}ms, got ${total}ms` : null;
      },
    };
  },

  outputContains(expected: string): ITraceAssertion {
    return {
      evaluate(ctx) {
        const found = flattenSteps(ctx.steps).some(
          (s) => s.output != null && String(s.output).includes(expected)
        );
        return found ? null : `OutputContains: no step output contains '${expected}'`;
      },
    };
  },

  toolCalled(toolName: string): ITraceAssertion {
    return {
      evaluate(ctx) {
        const found = flattenSteps(ctx.steps).some(
          (s) => s.type === "tool" && s.name === toolName
        );
        return found ? null : `ToolCalled: no tool step named '${toolName}' was found`;
      },
    };
  },
};
