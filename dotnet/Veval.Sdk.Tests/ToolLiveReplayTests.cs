using FluentAssertions;
using Veval.Sdk;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

/// <summary>Tool-live replay: tools run for real against their recorded inputs, the model is served from the recording.</summary>
public class ToolLiveReplayTests
{
    private static StepData Recorded(string name, string type, object? input, object? output) => new()
    {
        StepId = Guid.NewGuid().ToString(), Name = name, Type = type, Input = input, Output = output, Status = "success",
        StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow, DurationMs = 10,
    };

    /// <summary>A recorded run: the model picks a product, a pure tool formats its price, the model writes the answer.</summary>
    private static TraceData Trace() => new()
    {
        TraceId = "tr_quote",
        AgentName = "quote",
        Input = "how much is the lamp?",
        Status = "success",
        Steps = new List<StepData>
        {
            Recorded("pick_product", "llm", "how much is the lamp?", "lamp"),
            Recorded("format_price", "tool", "lamp", "EUR 12.50"),
            Recorded("answer", "llm", "price: EUR 12.50", "The lamp costs EUR 12.50."),
        },
    };

    private static VevalSdk Sdk() => new(new VevalOptions { ApiKey = "test" });

    private static readonly ReplayOptions ToolLive = new() { LiveToolSteps = true };

    /// <summary>The agent under test; <paramref name="format"/> is the tool's implementation.</summary>
    private static Func<VevalExecutionContext, Task<string>> Agent(Func<string, string> format, Action? onModel = null) => async ctx =>
    {
        var product = await ctx.TrackStepAsync("pick_product", (string)ctx.Input!, () =>
        {
            onModel?.Invoke();
            return Task.FromResult("live model");
        }, new StepOptions { Type = StepType.Llm });
        var price = await ctx.TrackStepAsync("format_price", product, () => Task.FromResult(format(product)),
            new StepOptions { Type = StepType.Tool });
        return await ctx.TrackStepAsync("answer", $"price: {price}", () =>
        {
            onModel?.Invoke();
            return Task.FromResult("live model");
        }, new StepOptions { Type = StepType.Llm });
    };

    [Fact]
    public async Task UnchangedTool_RunsForReal_ModelFromRecording_Passes()
    {
        var toolRuns = 0;
        var modelCalls = 0;
        var result = await Sdk().ReplayAsync(Trace(), Agent(p => { toolRuns++; return "EUR 12.50"; }, () => modelCalls++), ToolLive);

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        toolRuns.Should().Be(1);
        modelCalls.Should().Be(0);
        result.Output.Should().Be("The lamp costs EUR 12.50.");
        result.Fidelity!.ToolChecks.Should().ContainSingle().Which.Changed.Should().BeFalse();
        result.ReplayedContext!.Steps[1].Metadata["_source"].Should().Be("live_tool");
        result.Summary.Should().Contain("1 tools replayed, 0 not replayed, 0 outputs changed");
    }

    [Fact]
    public async Task ChangedTool_FailsWithBothOutputs_AndTheAgentGetsTheNewOutput()
    {
        var result = await Sdk().ReplayAsync(Trace(), Agent(_ => "EUR 1250"), ToolLive);

        result.Outcome.Should().Be(ReplayOutcome.Failed);
        result.Failures.Should().Contain(
            "Tool output changed: 'format_price' (recorded step 2) ran with its recorded input and returned \"EUR 1250\" instead of \"EUR 12.50\".");
        result.Fidelity!.ToolOutputChanges.Should().ContainSingle().Which.ActualOutput.Should().Be("EUR 1250");
        result.ReplayedContext!.Steps[2].Input.Should().Be("price: EUR 1250", "the agent's code ran on the new output");
        result.Summary.Should().Contain("1 output changed");
    }

    [Fact]
    public async Task ObjectOutputs_CompareIgnoringKeyOrder_AndIgnoredFields()
    {
        var trace = Trace();
        trace.Steps[1].Output = new Dictionary<string, object> { ["amount"] = 12.5, ["currency"] = "EUR", ["request_id"] = "r1" };
        var options = new ReplayOptions
        {
            LiveToolSteps = true,
            StepRules = { ["format_price"] = new ReplayStepRule { IgnoreOutputFields = { "request_id" } } },
        };

        var result = await Sdk().ReplayAsync(trace, async ctx =>
        {
            var product = await ctx.TrackStepAsync("pick_product", (string)ctx.Input!, () => Task.FromResult("x"));
            var price = await ctx.TrackStepAsync<object>("format_price", product,
                () => Task.FromResult<object>(new { request_id = "r2", currency = "EUR", amount = 12.5 }),
                new StepOptions { Type = StepType.Tool });
            return "done";
        }, options);

        result.Fidelity!.ToolOutputChanges.Should().BeEmpty();
        result.Failures.Should().NotContain(f => f.StartsWith("Tool output changed"));
    }

    [Fact]
    public async Task ToolCalledWithAnUnrecordedInput_IsInconclusive()
    {
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            await ctx.TrackStepAsync("pick_product", (string)ctx.Input!, () => Task.FromResult("x"));
            return await ctx.TrackStepAsync("format_price", "LAMP", () => Task.FromResult("EUR 12.50"),
                new StepOptions { Type = StepType.Tool });
        }, ToolLive);

        result.Outcome.Should().Be(ReplayOutcome.Inconclusive);
        result.Inconclusive!.Reason.Should().Be(ReplayStopReason.Diverged);
        result.Inconclusive.Message.Should().StartWith("Tool-live replay stopped at step 2 ('format_price')")
            .And.Contain("The model is served from the recording");
    }

    [Fact]
    public async Task ToolTheRecordingNeverCalled_IsInconclusive()
    {
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            await ctx.TrackStepAsync("lookup_stock", "lamp", () => Task.FromResult(3), new StepOptions { Type = StepType.Tool });
            return "";
        }, ToolLive);

        result.Outcome.Should().Be(ReplayOutcome.Inconclusive);
        result.Inconclusive!.Reason.Should().Be(ReplayStopReason.NoRecording);
    }

    [Fact]
    public async Task ToolThatReadsExternalState_IsServed_AndReportedNotReplayed_NeverAsAPass()
    {
        var ran = false;
        var options = new ReplayOptions
        {
            LiveToolSteps = true,
            StepRules = { ["format_price"] = new ReplayStepRule { ReadsExternalState = true } },
        };

        var result = await Sdk().ReplayAsync(Trace(), Agent(_ => { ran = true; return "live"; }), options);

        ran.Should().BeFalse();
        result.Fidelity!.NotReplayed.Should().Be(1);
        result.Outcome.Should().Be(ReplayOutcome.Failed, "with no tool replayed, nothing was checked");
        result.Failures.Should().ContainSingle().Which.Should()
            .StartWith("Tool-live replay of trace tr_quote ran no tool").And.Contain("except 1 that read external state");
    }

    [Fact]
    public async Task ToolThatNowThrows_IsAChange()
    {
        var result = await Sdk().ReplayAsync(Trace(), Agent(_ => throw new FormatException("bad currency")), ToolLive);

        result.Failures.Should().Contain(
            "Tool output changed: 'format_price' (recorded step 2) ran with its recorded input and threw (bad currency) where the recording returned \"EUR 12.50\".");
    }

    [Fact]
    public async Task ToolThatFailedInTheRecording_AndStillFails_IsNotAChange()
    {
        var trace = Trace();
        trace.Steps[1].Status = "error";
        trace.Steps[1].Error = "bad currency";
        trace.Steps[1].Output = null;

        var result = await Sdk().ReplayAsync(trace, async ctx =>
        {
            var product = await ctx.TrackStepAsync("pick_product", (string)ctx.Input!, () => Task.FromResult("x"));
            try
            {
                await ctx.TrackStepAsync<string>("format_price", product, () => throw new FormatException("bad currency"),
                    new StepOptions { Type = StepType.Tool });
            }
            catch (FormatException) { }
            return "fallback";
        }, ToolLive);

        result.Fidelity!.ToolChecks.Should().ContainSingle().Which.Changed.Should().BeFalse();
        result.Failures.Should().NotContain(f => f.StartsWith("Tool output changed"));
    }

    [Fact]
    public async Task UntypedSteps_AreServedFromTheRecording()
    {
        var trace = Trace();
        trace.Steps.Insert(2, Recorded("narrate", "custom", "EUR 12.50", "narrated"));
        var narrated = false;

        var result = await Sdk().ReplayAsync(trace, async ctx =>
        {
            var product = await ctx.TrackStepAsync("pick_product", (string)ctx.Input!, () => Task.FromResult("x"));
            var price = await ctx.TrackStepAsync("format_price", product, () => Task.FromResult("EUR 12.50"),
                new StepOptions { Type = StepType.Tool });
            await ctx.TrackStepAsync("narrate", price, () => { narrated = true; return Task.FromResult("live"); });
            return await ctx.TrackStepAsync("answer", $"price: {price}", () => Task.FromResult("live"));
        }, ToolLive);

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        narrated.Should().BeFalse();
    }

    [Fact]
    public async Task LiveToolStepsWithLiveModelSteps_Throws()
    {
        var act = () => Sdk().ReplayAsync(Trace(), Agent(p => p), new ReplayOptions { LiveToolSteps = true, LiveModelSteps = true });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*can't be combined*");
    }

    [Fact]
    public async Task FaultsWorkInToolLiveReplay()
    {
        var calls = 0;
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            var product = await ctx.TrackStepAsync("pick_product", (string)ctx.Input!, () => Task.FromResult("x"));
            string price;
            try { price = await ctx.TrackStepAsync("format_price", product, () => { calls++; return Task.FromResult("EUR 12.50"); }, new StepOptions { Type = StepType.Tool }); }
            catch (TimeoutException) { price = await ctx.TrackStepAsync("format_price", product, () => { calls++; return Task.FromResult("EUR 12.50"); }, new StepOptions { Type = StepType.Tool }); }
            catch (TaskCanceledException) { price = await ctx.TrackStepAsync("format_price", product, () => { calls++; return Task.FromResult("EUR 12.50"); }, new StepOptions { Type = StepType.Tool }); }
            return await ctx.TrackStepAsync("answer", $"price: {price}", () => Task.FromResult("live"));
        }, new ReplayOptions { LiveToolSteps = true, Faults = { ReplayFault.Timeout("format_price").Times(1) } });

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        calls.Should().Be(1, "the faulted call didn't run; the retry ran the tool for real");
    }

    [Fact]
    public async Task Scenario_LiveToolSteps_RunsToolsInTheTestDouble()
    {
        var toolRuns = 0;
        var veval = new VevalTestSdk(new VevalOptions { ApiKey = "test" }).WithReplay(Trace());

        var run = await veval.RunScenarioAsync("quote", Agent(_ => { toolRuns++; return "EUR 1250"; }), [],
            [new ScenarioItem { TraceId = "tr_quote" }],
            new ScenarioOptions { LiveToolSteps = true });

        toolRuns.Should().Be(1);
        run.Passed.Should().BeFalse();
        run.Results.Single().Failures.Should().Contain(f => f.StartsWith("Tool output changed: 'format_price'"));
    }

    [Fact]
    public void ScenarioReplayOptions_ToolLiveWinsOverMocked()
    {
        var options = VevalSdk.ScenarioReplayOptions(new ScenarioItem { MockModelSteps = true, LiveToolSteps = true },
            new ScenarioOptions(), []);

        options.LiveToolSteps.Should().BeTrue();
        options.MockLlmResponses.Should().BeFalse();
        options.LiveModelSteps.Should().BeFalse();
    }
}
