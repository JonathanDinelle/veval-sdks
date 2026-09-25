using FluentAssertions;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

/// <summary>Custom assertions written outside the SDK can read a run's steps, metadata, and judgments.</summary>
public class ContextVisibilityTests
{
    // The custom assertion from the README, compiled here so the example can't drift from the API.
    private class NoSecretsInPrompts : ITraceAssertion
    {
        public Task<string?> EvaluateAsync(VevalExecutionContext ctx)
        {
            var leaked = ctx.Steps.FirstOrDefault(s => s.Input?.ToString()?.Contains("sk-") == true);
            return Task.FromResult(leaked is null ? null : $"Step '{leaked.Name}' sent a secret");
        }
    }

    [Fact]
    public async Task CustomAssertion_ReadsStepInputs()
    {
        var ctx = new VevalExecutionContext("tr", null);
        await ctx.TrackStepAsync("call-llm", "key: sk-123", () => Task.FromResult("ok"));

        (await new NoSecretsInPrompts().EvaluateAsync(ctx)).Should().Be("Step 'call-llm' sent a secret");
    }

    [Fact]
    public async Task Context_ExposesStepsMetadataAndJudgments()
    {
        var ctx = new VevalExecutionContext("tr", null);
        ctx.SetMetadata("user_id", "u_1");
        await ctx.TrackStepAsync("write", "prompt", step =>
        {
            step.SetMeta("type", "llm");
            step.SetMeta("cost_usd", 0.01m);
            return Task.FromResult("answer");
        });
        var judge = new VevalTestSdk(new VevalOptions { ApiKey = "test" }).WithJudgeMock("polite", passed: true, score: 0.9);
        await TraceAssert.Judge(judge, "polite").EvaluateAsync(ctx);

        ctx.Steps.Should().ContainSingle(s => s.Name == "write" && s.Type == "llm" && s.CostUsd == 0.01m && (string?)s.Output == "answer");
        ctx.TraceMeta["user_id"].Should().Be("u_1");
        ctx.Judgments.Should().ContainSingle(j => j.Criteria == "polite" && j.Score == 0.9);
    }
}
