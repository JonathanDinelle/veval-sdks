using FluentAssertions;
using Veval.Sdk;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

/// <summary>Live replay: model steps run for real, every other step is served from the recording.</summary>
public class LiveReplayTests
{
    private const string Prompt = "List overdue items first.";
    private const string RegressedPrompt = "List items alphabetically.";

    private static StepData Recorded(string id, string name, string type, object? input, object? output) =>
        new() { StepId = id, Name = name, Type = type, Input = input, Output = output, Status = "success", StartedAt = DateTime.UtcNow };

    /// <summary>A production trace: a portal visit (tool) and a model call that wrote the briefing.</summary>
    private static TraceData BriefingTrace() => new()
    {
        TraceId = "tr_briefing",
        AgentName = "briefing",
        Input = "today",
        Status = "success",
        Steps =
        [
            Recorded("s1", "visit_portal", "tool", "today", "Dentist (overdue); Taxes"),
            Recorded("s2", "write_briefing", "llm", Prompt, "OVERDUE: Dentist. Then: Taxes."),
        ],
    };

    /// <summary>Stands in for the model: it only puts overdue items first when the prompt asks for it.</summary>
    private static string FakeModel(string prompt, string items) =>
        prompt.Contains("overdue") ? "OVERDUE: Dentist. Then: Taxes." : "Dentist. Taxes.";

    private static Func<VevalExecutionContext, Task<string>> BriefingAgent(string prompt, Action? onPortalVisit = null) => async ctx =>
    {
        var items = await ctx.TrackStepAsync("visit_portal", "today", () =>
        {
            onPortalVisit?.Invoke();
            return Task.FromResult("live portal data");
        });
        return await ctx.TrackStepAsync("write_briefing", prompt, () => Task.FromResult(FakeModel(prompt, items)));
    };

    /// <summary>Replays the trace the way <c>VevalSdk.RunScenarioAsync</c> replays a trace-backed item.</summary>
    private static Task<ReplayResult> ReplayAsScenarioItem(string prompt, ScenarioItem item, bool scenarioMock = false, Action? onPortalVisit = null) =>
        new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(BriefingTrace(), BriefingAgent(prompt, onPortalVisit),
            VevalSdk.ScenarioReplayOptions(item, scenarioMock, [TraceAssert.OutputContains("OVERDUE")]));

    [Fact]
    public async Task TraceBackedItem_CatchesARegressedPrompt_AndPassesTheUnchangedOne()
    {
        var portalVisits = 0;
        var item = new ScenarioItem { TraceId = "tr_briefing" };

        var unchanged = await ReplayAsScenarioItem(Prompt, item, onPortalVisit: () => portalVisits++);
        var regressed = await ReplayAsScenarioItem(RegressedPrompt, item, onPortalVisit: () => portalVisits++);

        unchanged.Passed.Should().BeTrue(string.Join("; ", unchanged.Failures));
        regressed.Passed.Should().BeFalse();
        regressed.Failures.Should().Contain(f => f.Contains("OutputContains"));
        portalVisits.Should().Be(0, "tool steps are served from the recording");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TraceBackedItem_MockedByTheItemOrTheScenario_CannotSeeThePromptChange(bool itemMock, bool scenarioMock)
    {
        var regressed = await ReplayAsScenarioItem(RegressedPrompt,
            new ScenarioItem { TraceId = "tr_briefing", MockModelSteps = itemMock }, scenarioMock);
        regressed.Passed.Should().BeTrue("mocked replay returns the old prompt's output");
        regressed.Fidelity!.Live.Should().Be(0);
    }

    [Fact]
    public async Task TestSdkScenario_AlwaysReplaysMocked()
    {
        var trace = BriefingTrace();
        var modelCalls = 0;
        var sdk = new VevalTestSdk(new VevalOptions { ApiKey = "test" }).WithReplay(trace);

        var result = await sdk.RunScenarioAsync("briefing", async ctx =>
            {
                await ctx.TrackStepAsync("visit_portal", "today", () => Task.FromResult("live"));
                return await ctx.TrackStepAsync("write_briefing", RegressedPrompt, () => { modelCalls++; return Task.FromResult("live"); });
            },
            [TraceAssert.OutputContains("OVERDUE")],
            [new ScenarioItem { TraceId = trace.TraceId }]);

        result.Passed.Should().BeTrue("the test double serves the recorded model output");
        modelCalls.Should().Be(0, "the test double never makes a live model call");
    }

    [Fact]
    public async Task LiveReplay_ReportsModelStepsAsLive()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var result = await sdk.ReplayAsync(BriefingTrace(), BriefingAgent(Prompt), new ReplayOptions { LiveModelSteps = true });

        result.Passed.Should().BeTrue();
        result.Fidelity!.Live.Should().Be(1);
        result.Fidelity.Bindings.Select(b => (b.StepName, b.Kind))
            .Should().Equal(("visit_portal", ReplayBindingKind.Matched), ("write_briefing", ReplayBindingKind.Live));
    }

    [Fact]
    public async Task LiveReplay_ServesUntypedStepsFromTheRecording()
    {
        var trace = BriefingTrace();
        trace.Steps.Insert(0, Recorded("s0", "load_profile", "custom", "u_1", "profile"));
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var ran = false;

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            var profile = await ctx.TrackStepAsync("load_profile", "u_1", () => { ran = true; return Task.FromResult("live"); });
            profile.Should().Be("profile");
            return await BriefingAgent(Prompt)(ctx);
        }, new ReplayOptions { LiveModelSteps = true });

        result.Passed.Should().BeTrue();
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task LiveReplay_FailsWhenNoStepRanLive()
    {
        var trace = BriefingTrace();
        trace.Steps[1].Type = "custom";  // the model step was never marked as llm
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });

        var result = await sdk.ReplayAsync(trace, BriefingAgent(RegressedPrompt), new ReplayOptions { LiveModelSteps = true });

        result.Passed.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("ran no step live") && f.Contains("\"type\", \"llm\""));
    }

    [Fact]
    public async Task LiveReplay_RunsAnExtraCallOfAModelStepLive()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });

        var result = await sdk.ReplayAsync(BriefingTrace(), async ctx =>
        {
            await BriefingAgent(Prompt)(ctx);
            return await ctx.TrackStepAsync("write_briefing", "Shorten it.", () => Task.FromResult("OVERDUE: Dentist."));
        }, new ReplayOptions { LiveModelSteps = true });

        result.Passed.Should().BeTrue();
        result.Output.Should().Be("OVERDUE: Dentist.");
        result.Fidelity!.Bindings.Last().Should().Match<ReplayBinding>(b => b.Kind == ReplayBindingKind.Live && b.RecordedIndex == null);
    }

    [Fact]
    public async Task LiveReplay_RefusesToRunAnUnrecordedToolCall()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var charged = false;

        var result = await sdk.ReplayAsync(BriefingTrace(), async ctx =>
        {
            await BriefingAgent(Prompt)(ctx);
            return await ctx.TrackStepAsync("charge_card", 42, () => { charged = true; return Task.FromResult("ok"); });
        }, new ReplayOptions { LiveModelSteps = true });

        result.Passed.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("Live replay: step 'charge_card' has no recording"));
        charged.Should().BeFalse();
    }

    [Fact]
    public async Task LiveReplay_ReportsUnusedRecordingsWithoutFailing()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });

        var result = await sdk.ReplayAsync(BriefingTrace(),
            ctx => ctx.TrackStepAsync("write_briefing", Prompt, () => Task.FromResult("OVERDUE: Dentist.")),
            new ReplayOptions { LiveModelSteps = true });

        result.Passed.Should().BeTrue("a live model may legitimately take a different path");
        result.Fidelity!.Unused.Should().ContainSingle(u => u.StepName == "visit_portal");
    }
}
