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

    /// <summary>
    /// Scores the trace's output against a rubric using an LLM judge, evaluated server-side.
    /// </summary>
    public static ITraceAssertion Judge(IVevalSdk veval, string criteria, JudgeOptions? options = null) =>
        new JudgeAssertion(veval, criteria, options);

    /// <summary>
    /// Fails when the run's step sequence or step inputs differ from the snapshot — e.g. a changed prompt,
    /// a dropped or repeated call. The failure message lists every change with a diff.
    /// </summary>
    public static ITraceAssertion MatchesSnapshot(SnapshotData snapshot, SnapshotOptions? options = null) =>
        new SnapshotAssertion(_ => Task.FromResult<SnapshotData?>(snapshot), snapshot.Name, options);

    /// <summary>
    /// Like <see cref="MatchesSnapshot(SnapshotData, SnapshotOptions?)"/>, loading the named baseline stored in Veval
    /// (see <see cref="IVevalSdk.SaveSnapshotAsync(string, string)"/>). A missing baseline fails the assertion.
    /// </summary>
    public static ITraceAssertion MatchesSnapshot(IVevalSdk veval, string snapshotName, SnapshotOptions? options = null) =>
        new SnapshotAssertion(_ => veval.GetSnapshotAsync(snapshotName), snapshotName, options);

    private class MaxStepsAssertion : ITraceAssertion
    {
        private readonly int _max;
        public MaxStepsAssertion(int max) => _max = max;

        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            var count = CountSteps(ctx.Steps);
            return Task.FromResult(count > _max ? $"MaxSteps: expected at most {_max} steps, got {count}" : null);
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
        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            var errors = FindErrors(ctx.Steps);
            return Task.FromResult(errors.Count > 0
                ? $"NoErrors: found {errors.Count} step(s) with error status: {string.Join(", ", errors)}"
                : null);
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

        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            return Task.FromResult(HasStep(ctx.Steps, _stepName)
                ? null
                : $"StepExists: step '{_stepName}' not found");
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

        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            var totalCost = SumCost(ctx.Steps);
            return Task.FromResult(totalCost > _maxCost
                ? $"MaxCost: expected at most {_maxCost}, got {totalCost}"
                : null);
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

        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            var totalMs = SumDuration(ctx.Steps);
            return Task.FromResult(totalMs > _maxMs
                ? $"MaxDuration: expected at most {_maxMs}ms, got {totalMs}ms"
                : null);
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

        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            return Task.FromResult(HasOutputContaining(ctx.Steps, _expected)
                ? null
                : $"OutputContains: no step output contains '{_expected}'");
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

        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            return Task.FromResult(HasToolCall(ctx.Steps, _toolName)
                ? null
                : $"ToolCalled: no tool step named '{_toolName}' was found");
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

    private class JudgeAssertion : ITraceAssertion
    {
        private readonly IVevalSdk _veval;
        private readonly string _criteria;
        private readonly JudgeOptions? _options;

        public JudgeAssertion(IVevalSdk veval, string criteria, JudgeOptions? options)
        {
            _veval = veval;
            _criteria = criteria;
            _options = options;
        }

        public async Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            JudgeResult result;
            try
            {
                result = await _veval.JudgeAsync(_criteria, ctx, _options);
            }
            catch (Exception ex)
            {
                // Never let a network/API failure during judging silently pass a test.
                return $"Judge: evaluation failed — {ex.Message}";
            }

            ctx.RecordJudgment(_criteria, result.Score, result.Passed, result.Reasoning);

            return result.Passed
                ? null
                : $"Judge: {_criteria} (score {result.Score:0.00}) — {result.Reasoning}";
        }
    }

    private class SnapshotAssertion : ITraceAssertion
    {
        private readonly Func<VevalExecutionContext, Task<SnapshotData?>> _load;
        private readonly string? _name;
        private readonly SnapshotOptions? _options;

        public SnapshotAssertion(Func<VevalExecutionContext, Task<SnapshotData?>> load, string? name, SnapshotOptions? options)
        {
            _load = load;
            _name = name;
            _options = options;
        }

        public async Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            SnapshotData? snapshot;
            try
            {
                snapshot = await _load(ctx);
            }
            catch (Exception ex)
            {
                // A baseline we couldn't load must fail loudly, never pass vacuously.
                return $"MatchesSnapshot: could not load snapshot '{_name}' — {ex.Message}";
            }

            if (snapshot is null)
                return $"MatchesSnapshot: no snapshot named '{_name}'. Save one with SaveSnapshotAsync first.";

            var diff = SnapshotComparer.Compare(snapshot, ctx, _options);
            return diff.HasChanges ? "MatchesSnapshot: " + diff.Summary(_name ?? snapshot.Name) : null;
        }
    }
}
