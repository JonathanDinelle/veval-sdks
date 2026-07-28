namespace Veval.Sdk.Assertions;

public static class TraceAssert
{
    public static ITraceAssertion MaxSteps(int max) => new MaxStepsAssertion(max);
    public static ITraceAssertion NoErrors() => new NoErrorsAssertion();
    public static ITraceAssertion StepExists(string stepName) => new StepExistsAssertion(stepName);
    public static ITraceAssertion MaxCost(decimal maxCost) => new MaxCostAssertion(maxCost);
    public static ITraceAssertion MaxDuration(int maxMs) => new MaxDurationAssertion(maxMs);
    public static ITraceAssertion OutputContains(string expected) => new OutputContainsAssertion(expected);
    public static ITraceAssertion ToolCalled(string toolName) => new ToolCalledAssertion(toolName);

    private class MaxStepsAssertion : ITraceAssertion
    {
        private readonly int _max;
        public MaxStepsAssertion(int max) => _max = max;

        public string? Evaluate(VevalExecutionContext ctx)
        {
            var count = CountSteps(ctx.Steps);
            return count > _max ? $"MaxSteps: expected at most {_max} steps, got {count}" : null;
        }

        private static int CountSteps(IReadOnlyList<Step> steps)
        {
            var count = steps.Count;
            foreach (var s in steps)
                count += CountSteps(s.Children);
            return count;
        }
    }

    private class NoErrorsAssertion : ITraceAssertion
    {
        public string? Evaluate(VevalExecutionContext ctx)
        {
            var errors = FindErrors(ctx.Steps);
            return errors.Count > 0
                ? $"NoErrors: found {errors.Count} step(s) with error status: {string.Join(", ", errors)}"
                : null;
        }

        private static List<string> FindErrors(IReadOnlyList<Step> steps)
        {
            var errors = new List<string>();
            foreach (var s in steps)
            {
                if (s.Status == "error")
                    errors.Add(s.Name);
                errors.AddRange(FindErrors(s.Children));
            }
            return errors;
        }
    }

    private class StepExistsAssertion : ITraceAssertion
    {
        private readonly string _stepName;
        public StepExistsAssertion(string stepName) => _stepName = stepName;

        public string? Evaluate(VevalExecutionContext ctx)
        {
            return HasStep(ctx.Steps, _stepName)
                ? null
                : $"StepExists: step '{_stepName}' not found";
        }

        private static bool HasStep(IReadOnlyList<Step> steps, string name)
        {
            foreach (var s in steps)
            {
                if (s.Name == name) return true;
                if (HasStep(s.Children, name)) return true;
            }
            return false;
        }
    }

    private class MaxCostAssertion : ITraceAssertion
    {
        private readonly decimal _maxCost;
        public MaxCostAssertion(decimal maxCost) => _maxCost = maxCost;

        public string? Evaluate(VevalExecutionContext ctx)
        {
            var totalCost = SumCost(ctx.Steps);
            return totalCost > _maxCost
                ? $"MaxCost: expected at most {_maxCost}, got {totalCost}"
                : null;
        }

        private static decimal SumCost(IReadOnlyList<Step> steps)
        {
            var total = 0m;
            foreach (var s in steps)
            {
                total += s.CostUsd ?? 0;
                total += SumCost(s.Children);
            }
            return total;
        }
    }

    private class MaxDurationAssertion : ITraceAssertion
    {
        private readonly int _maxMs;
        public MaxDurationAssertion(int maxMs) => _maxMs = maxMs;

        public string? Evaluate(VevalExecutionContext ctx)
        {
            var totalMs = SumDuration(ctx.Steps);
            return totalMs > _maxMs
                ? $"MaxDuration: expected at most {_maxMs}ms, got {totalMs}ms"
                : null;
        }

        private static int SumDuration(IReadOnlyList<Step> steps)
        {
            var total = 0;
            foreach (var s in steps)
            {
                total += s.DurationMs ?? 0;
                total += SumDuration(s.Children);
            }
            return total;
        }
    }

    private class OutputContainsAssertion : ITraceAssertion
    {
        private readonly string _expected;
        public OutputContainsAssertion(string expected) => _expected = expected;

        public string? Evaluate(VevalExecutionContext ctx)
        {
            return HasOutputContaining(ctx.Steps, _expected)
                ? null
                : $"OutputContains: no step output contains '{_expected}'";
        }

        private static bool HasOutputContaining(IReadOnlyList<Step> steps, string expected)
        {
            foreach (var s in steps)
            {
                if (s.Output?.ToString()?.Contains(expected) == true) return true;
                if (HasOutputContaining(s.Children, expected)) return true;
            }
            return false;
        }
    }

    private class ToolCalledAssertion : ITraceAssertion
    {
        private readonly string _toolName;
        public ToolCalledAssertion(string toolName) => _toolName = toolName;

        public string? Evaluate(VevalExecutionContext ctx)
        {
            return HasToolCall(ctx.Steps, _toolName)
                ? null
                : $"ToolCalled: no tool step named '{_toolName}' was found";
        }

        private static bool HasToolCall(IReadOnlyList<Step> steps, string name)
        {
            foreach (var s in steps)
            {
                if (s.Type == "tool" && s.Name == name) return true;
                if (HasToolCall(s.Children, name)) return true;
            }
            return false;
        }
    }
}
