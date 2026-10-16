using FluentAssertions;
using Veval.Sdk;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

public class ScenarioRunnerTests
{
    private static VevalSdk MakeSdk() =>
        new VevalSdk(new VevalOptions { ApiKey = "test" });

    [Fact]
    public async Task RunScenarioAsync_ScenarioAssertionAndItemAssertionBothEvaluated()
    {
        var sdk = MakeSdk();
        var result = await sdk.RunScenarioAsync(
            "merge-test",
            async ctx =>
            {
                await ctx.TrackStepAsync("step-a", null, () => Task.FromResult("out"));
                return "done";
            },
            scenarioAssertions: new[] { TraceAssert.NoErrors() },
            items: new[]
            {
                new ScenarioItem
                {
                    Input = "test-input",
                    Assertions = new[] { TraceAssert.StepExists("step-a") }
                }
            }
        );
        result.Results.Should().HaveCount(1);
        result.Results[0].Passed.Should().BeTrue();
        result.Results[0].Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task RunScenarioAsync_ScenarioAssertionFails_ItemFails()
    {
        var sdk = MakeSdk();
        var result = await sdk.RunScenarioAsync(
            "fail-test",
            async ctx =>
            {
                for (var i = 0; i < 5; i++)
                    await ctx.TrackStepAsync($"step-{i}", null, () => Task.FromResult("x"));
                return "done";
            },
            scenarioAssertions: new[] { TraceAssert.MaxSteps(2) },
            items: new[] { new ScenarioItem { Input = "any" } }
        );
        result.Passed.Should().BeFalse();
        result.Results[0].Failures.Should().Contain(f => f.Contains("MaxSteps"));
    }

    [Fact]
    public async Task RunScenarioAsync_ItemWithNoTraceIdOrInput_Fails()
    {
        var sdk = MakeSdk();
        var result = await sdk.RunScenarioAsync(
            "bad-item-test",
            async ctx => { return "done"; },
            scenarioAssertions: Array.Empty<ITraceAssertion>(),
            items: new[] { new ScenarioItem { Name = "broken item" } }
        );
        result.Passed.Should().BeFalse();
        result.Results[0].Failures.Should().Contain(f => f.Contains("TraceId or Input"));
    }

    [Fact]
    public void ScenarioRunResult_PassCountAndFailCount_Correct()
    {
        var result = new ScenarioRunResult
        {
            Results = new List<ItemRunResult>
            {
                new() { Item = new ScenarioItem(), Failures = new List<string>() },
                new() { Item = new ScenarioItem(), Failures = new List<string> { "fail" } },
                new() { Item = new ScenarioItem(), Failures = new List<string>() },
            }
        };
        result.PassCount.Should().Be(2);
        result.FailCount.Should().Be(1);
        result.Passed.Should().BeFalse();
    }
}
