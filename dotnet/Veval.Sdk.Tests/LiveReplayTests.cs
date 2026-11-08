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
            VevalSdk.ScenarioReplayOptions(item, new ScenarioOptions { MockModelSteps = scenarioMock }, [TraceAssert.OutputContains("OVERDUE")]));

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
    public async Task LiveReplay_StopsInconclusiveOnAnUnrecordedToolCall()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var charged = false;

        var result = await sdk.ReplayAsync(BriefingTrace(), async ctx =>
        {
            await BriefingAgent(Prompt)(ctx);
            return await ctx.TrackStepAsync("charge_card", 42, () => { charged = true; return Task.FromResult("ok"); });
        }, new ReplayOptions { LiveModelSteps = true, Assertions = [TraceAssert.OutputContains("never")] });

        charged.Should().BeFalse();
        result.Outcome.Should().Be(ReplayOutcome.Inconclusive);
        result.Passed.Should().BeFalse();
        result.Failures.Should().BeEmpty("a stopped replay can't back up a verdict, so assertions are not evaluated");
        result.Inconclusive!.Should().Match<ReplayStop>(s =>
            s.Step == 3 && s.StepName == "charge_card" && s.Reason == ReplayStopReason.NoRecording);
        result.Inconclusive!.Message.Should().Contain("the trace never recorded it").And.Contain("replay mocked to test this change");
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

    /// <summary>
    /// A browser trace: the model fills a field, reads the page, and narrates what it does. Narration's text follows
    /// the model's wording and doesn't change the page.
    /// </summary>
    private static TraceData BrowserTrace() => new()
    {
        TraceId = "tr_browser",
        AgentName = "browser",
        Input = "book",
        Status = "success",
        Steps =
        [
            Recorded("s1", "plan", "llm", "book", "fill date 2026-11-02"),
            Recorded("s2", "narrate", "custom", new { text = "Filling the date" }, "ok"),
            Recorded("s3", "fill", "tool", new { field = "date", value = "2026-11-02" }, "filled"),
            Recorded("s4", "page_snapshot", "tool", new { reasoning = "check the form", tab = 1 }, "<form date=2026-11-02>"),
            Recorded("s5", "answer", "llm", "<form date=2026-11-02>", "Booked for Nov 2."),
        ],
    };

    /// <summary>The browser agent; each argument is what the live model would now choose at that point.</summary>
    private static Func<VevalExecutionContext, Task<string>> BrowserAgent(
        string date = "2026-11-02", string narration = "Filling the date", string reasoning = "check the form",
        List<string>? toolsRun = null) => async ctx =>
    {
        await ctx.TrackStepAsync("plan", "book", () => Task.FromResult($"fill date {date}"));
        await ctx.TrackStepAsync("narrate", new { text = narration }, () => { toolsRun?.Add("narrate"); return Task.FromResult("ok"); });
        await ctx.TrackStepAsync("fill", new { field = "date", value = date }, () => { toolsRun?.Add("fill"); return Task.FromResult("filled"); });
        var page = await ctx.TrackStepAsync("page_snapshot", new { reasoning, tab = 1 }, () => { toolsRun?.Add("page_snapshot"); return Task.FromResult("live page"); });
        return await ctx.TrackStepAsync("answer", page, () => Task.FromResult("Booked for Nov 2."));
    };

    private static Task<ReplayResult> LiveReplay(Func<VevalExecutionContext, Task<string>> agent, ReplayOptions? options = null)
    {
        options ??= new ReplayOptions();
        options.LiveModelSteps = true;
        options.Assertions = [TraceAssert.OutputContains("Booked")];
        return new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(BrowserTrace(), agent, options);
    }

    [Fact]
    public async Task LiveReplay_StopsWhereADivergedToolStepLeavesTheRecording()
    {
        var toolsRun = new List<string>();
        var result = await LiveReplay(BrowserAgent(date: "2026-11-03", toolsRun: toolsRun));

        result.Outcome.Should().Be(ReplayOutcome.Inconclusive);
        result.Failures.Should().BeEmpty();
        var stop = result.Inconclusive!;
        stop.Step.Should().Be(3);
        stop.StepName.Should().Be("fill");
        stop.Reason.Should().Be(ReplayStopReason.Diverged);
        stop.Message.Should().Contain("\"value\":\"2026-11-02\"").And.Contain("\"value\":\"2026-11-03\"")
            .And.Contain("re-record, or replay mocked to test this change");
        toolsRun.Should().BeEmpty("no tool runs for real in a live replay, and nothing runs after the stop");
        result.ReplayedContext!.Steps.Select(s => s.Name).Should().Equal("plan", "narrate", "fill");
        result.ReplayedContext.Steps.Last().Status.Should().Be("error");
        result.Status.Should().Be("inconclusive", "the replay trace neither passed nor failed, so it mustn't raise the error alert");
        result.Summary.Should().Be("inconclusive at step 3 'fill' — 2/5 recorded steps used, 0 diverged but served, 1 live, left the path");
    }

    [Fact]
    public async Task LiveReplay_PassesWhenOnlyANameMatchedNarrationStepChanges()
    {
        var result = await LiveReplay(BrowserAgent(narration: "Now I will fill in the date field"), new ReplayOptions
        {
            StopOnDivergenceTypes = new HashSet<string> { "tool", "custom" },
            StepRules = { ["narrate"] = new ReplayStepRule { MatchOnNameOnly = true } },
        });

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        result.Fidelity!.Diverged.Should().Be(0);
    }

    [Fact]
    public async Task LiveReplay_IgnoredInputFieldsDontCountAsDivergence()
    {
        var withoutRule = await LiveReplay(BrowserAgent(reasoning: "make sure the date stuck"));
        var withRule = await LiveReplay(BrowserAgent(reasoning: "make sure the date stuck"), new ReplayOptions
        {
            StepRules = { ["page_snapshot"] = new ReplayStepRule { IgnoreInputFields = new HashSet<string> { "reasoning" } } },
        });

        withoutRule.Inconclusive!.StepName.Should().Be("page_snapshot");
        withRule.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", withRule.Failures));
    }

    [Fact]
    public async Task LiveReplay_ServesADivergedUntypedStepByDefault_AndReportsIt()
    {
        var result = await LiveReplay(BrowserAgent(narration: "Typing the date"));

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        result.Fidelity!.Bindings.Should().ContainSingle(b => b.Kind == ReplayBindingKind.Diverged && b.StepName == "narrate");
    }

    [Fact]
    public async Task LiveReplay_AStepRuleOverridesTheTypeDefault()
    {
        var served = await LiveReplay(BrowserAgent(date: "2026-11-03"), new ReplayOptions
        {
            StepRules = { ["fill"] = new ReplayStepRule { OnDivergence = OnDivergence.Serve } },
        });
        var stopped = await LiveReplay(BrowserAgent(narration: "Typing the date"), new ReplayOptions
        {
            StepRules = { ["narrate"] = new ReplayStepRule { OnDivergence = OnDivergence.Stop } },
        });

        served.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", served.Failures));
        served.Fidelity!.Diverged.Should().Be(1);
        stopped.Inconclusive!.StepName.Should().Be("narrate");
    }

    [Fact]
    public async Task LiveReplay_StaysInconclusiveWhenTheAgentCatchesTheStop()
    {
        var laterStepThrew = false;
        var result = await LiveReplay(async ctx =>
        {
            try { await ctx.TrackStepAsync("fill", new { field = "date", value = "tomorrow" }, () => Task.FromResult("filled")); }
            catch (Exception) { /* an agent that retries or swallows tool errors */ }
            try { await ctx.TrackStepAsync("page_snapshot", new { reasoning = "check the form", tab = 1 }, () => Task.FromResult("live")); }
            catch (ReplayStoppedException) { laterStepThrew = true; }
            return "Booked anyway";
        });

        laterStepThrew.Should().BeTrue("every step after the stop throws it again");
        result.Outcome.Should().Be(ReplayOutcome.Inconclusive);
        result.Inconclusive!.StepName.Should().Be("fill");
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task MockedReplay_StillServesADivergedToolStep()
    {
        var result = await new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(
            BrowserTrace(), BrowserAgent(date: "2026-11-03"), new ReplayOptions { MockLlmResponses = true });

        result.Inconclusive.Should().BeNull("stopping on divergence is for live replays; mocked replay serves every step");
        result.Fidelity!.Bindings.Should().ContainSingle(b => b.Kind == ReplayBindingKind.Diverged && b.StepName == "fill");
    }

    [Fact]
    public void ScenarioReplayOptions_AnItemRuleReplacesTheScenarioRuleOfTheSameStep()
    {
        var item = new ScenarioItem
        {
            TraceId = "tr_browser",
            StepRules = { ["fill"] = new ReplayStepRule { OnDivergence = OnDivergence.Serve } },
        };
        var scenario = new ScenarioOptions
        {
            StepRules =
            {
                ["fill"] = new() { OnDivergence = OnDivergence.Stop },
                ["narrate"] = new() { MatchOnNameOnly = true },
            },
        };

        var options = VevalSdk.ScenarioReplayOptions(item, scenario, []);

        options.StepRules["fill"].OnDivergence.Should().Be(OnDivergence.Serve);
        options.StepRules["narrate"].MatchOnNameOnly.Should().BeTrue();
    }

    [Fact]
    public void ScenarioRunResult_CountsInconclusiveItemsApart()
    {
        var stop = new ReplayStop(3, "fill", ReplayStopReason.Diverged, null, null, "stopped");
        var result = new ScenarioRunResult
        {
            Results =
            [
                new ItemRunResult { Item = new ScenarioItem() },
                new ItemRunResult { Item = new ScenarioItem(), Failures = ["OutputContains failed"] },
                new ItemRunResult { Item = new ScenarioItem(), Inconclusive = stop },
            ],
        };

        result.Passed.Should().BeFalse();
        (result.PassCount, result.FailCount, result.InconclusiveCount).Should().Be((1, 1, 1));
        result.Results[2].Passed.Should().BeFalse();
    }

    private static ItemRunResult PassedItem() => new() { Item = new ScenarioItem() };

    private static ItemRunResult ReplayedItem() => new() { Item = new ScenarioItem(), Fidelity = new ReplayFidelity([]) };

    private static ItemRunResult InconclusiveItem() =>
        new() { Item = new ScenarioItem(), Inconclusive = new ReplayStop(3, "fill", ReplayStopReason.Diverged, null, null, "stopped") };

    [Fact]
    public void ScenarioRun_FailsOnAnyInconclusiveItemByDefault()
    {
        var result = new ScenarioRunResult { Results = [ReplayedItem(), ReplayedItem(), ReplayedItem(), InconclusiveItem()] };

        result.Passed.Should().BeFalse("a change that sends items off their recorded path mustn't look green");
        result.InconclusiveRate.Should().Be(0.25);
        result.Summary.Should().Be(
            "failed: too many inconclusive — 3 passed, 0 failed, 1 inconclusive (25% of replays > 0% allowed), 3/4 replays stayed on path");
    }

    [Fact]
    public void ScenarioRun_PassesWhenTheInconclusiveRateIsWithinTheThreshold()
    {
        var result = new ScenarioRunResult
        {
            MaxInconclusiveRate = 0.25,
            Results = [ReplayedItem(), ReplayedItem(), ReplayedItem(), InconclusiveItem()],
        };

        result.Passed.Should().BeTrue();
        result.Summary.Should().Be(
            "passed — 3 passed, 0 failed, 1 inconclusive (25% of replays, within 25% allowed), 3/4 replays stayed on path");
    }

    [Fact]
    public void ScenarioRun_SyntheticItemsDontDiluteTheInconclusiveRate()
    {
        // Only a replay can be inconclusive: two synthetic items mustn't turn one stop in two replays into 25%.
        var result = new ScenarioRunResult
        {
            MaxInconclusiveRate = 0.25,
            Results = [PassedItem(), PassedItem(), ReplayedItem(), InconclusiveItem()],
        };

        result.InconclusiveRate.Should().Be(0.5);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void ScenarioRun_NamesFailedItemsFirstWhenBothReasonsApply()
    {
        var failed = new ItemRunResult { Item = new ScenarioItem(), Failures = ["OutputContains failed"] };
        var result = new ScenarioRunResult { Results = [failed, InconclusiveItem()] };

        result.Summary.Should().StartWith("failed — 0 passed, 1 failed, 1 inconclusive");
    }

    [Fact]
    public void ScenarioRun_AFailedItemFailsTheRunWhateverTheThreshold()
    {
        var failed = new ItemRunResult { Item = new ScenarioItem(), Failures = ["OutputContains failed"] };
        var result = new ScenarioRunResult { MaxInconclusiveRate = 1, Results = [PassedItem(), failed] };

        result.Passed.Should().BeFalse();
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void ScenarioOptions_RejectsAThresholdOutsideZeroToOne(double rate)
    {
        var act = () => new ScenarioOptions { MaxInconclusiveRate = rate };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ScenarioRun_ReportsHowManyReplaysStayedOnPath()
    {
        var onPath = await LiveReplay(BrowserAgent());
        var offPath = await LiveReplay(BrowserAgent(date: "2026-11-03"));
        var result = new ScenarioRunResult
        {
            Results =
            [
                new ItemRunResult { Item = new ScenarioItem(), Fidelity = onPath.Fidelity },
                new ItemRunResult { Item = new ScenarioItem(), Fidelity = offPath.Fidelity, Inconclusive = offPath.Inconclusive },
                PassedItem(),
            ],
        };

        result.StayedOnPathRate.Should().Be(0.5, "the synthetic item wasn't replayed, so it doesn't count");
        result.Summary.Should().EndWith(", 1/2 replays stayed on path");
        onPath.Summary.Should().Be("passed — 5/5 recorded steps used, 0 diverged but served, 2 live, stayed on path");
    }

    [Fact]
    public void ScenarioRunPayload_PostsAThirdOutcomeAndTheStop()
    {
        var stop = new ReplayStop(3, "fill", ReplayStopReason.Diverged, new { value = "a" }, new { value = "b" }, "stopped");
        var result = new ScenarioRunResult
        {
            Results =
            [
                new ItemRunResult { Item = new ScenarioItem { Name = "ok" } },
                new ItemRunResult { Item = new ScenarioItem { Name = "bad" }, Failures = ["OutputContains failed"] },
                new ItemRunResult { Item = new ScenarioItem { Name = "off" }, Inconclusive = stop },
            ],
        };

        var json = System.Text.Json.JsonSerializer.SerializeToElement(Internal.ScenarioReport.RunPayload(result, "synthetic"));

        json.GetProperty("fail_count").GetInt32().Should().Be(1);
        json.GetProperty("inconclusive_count").GetInt32().Should().Be(1);
        json.GetProperty("max_inconclusive_rate").GetDouble().Should().Be(0);
        json.GetProperty("summary").GetString().Should().StartWith("failed — 1 passed, 1 failed, 1 inconclusive");
        var items = json.GetProperty("results").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("outcome").GetString()).Should().Equal("passed", "failed", "inconclusive");
        var off = items[2];
        off.GetProperty("passed").GetBoolean().Should().BeFalse();
        off.GetProperty("failures").GetArrayLength().Should().Be(0);
        off.GetProperty("summary").GetString().Should().Be("inconclusive at step 3 'fill'");
        var posted = off.GetProperty("inconclusive");
        posted.GetProperty("step").GetInt32().Should().Be(3);
        posted.GetProperty("step_name").GetString().Should().Be("fill");
        posted.GetProperty("reason").GetString().Should().Be("diverged");
        posted.GetProperty("recorded_input").GetProperty("value").GetString().Should().Be("a");
        posted.GetProperty("actual_input").GetProperty("value").GetString().Should().Be("b");
        posted.GetProperty("message").GetString().Should().Be("stopped");
        items[0].GetProperty("inconclusive").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task LiveReplay_IgnoresFieldsInsideAJsonStringInput()
    {
        // A model's tool-call arguments often arrive as a JSON string rather than an object.
        var trace = BrowserTrace();
        trace.Steps[3].Input = "{\"reasoning\":\"check the form\",\"tab\":1}";
        Func<VevalExecutionContext, Task<string>> agent = async ctx =>
        {
            await ctx.TrackStepAsync("plan", "book", () => Task.FromResult("fill"));
            await ctx.TrackStepAsync("narrate", new { text = "Filling the date" }, () => Task.FromResult("ok"));
            await ctx.TrackStepAsync("fill", new { field = "date", value = "2026-11-02" }, () => Task.FromResult("filled"));
            var page = await ctx.TrackStepAsync("page_snapshot", "{\"tab\": 1, \"reasoning\": \"make sure the date stuck\"}",
                () => Task.FromResult("live"));
            return await ctx.TrackStepAsync("answer", page, () => Task.FromResult("Booked for Nov 2."));
        };

        var result = await new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(trace, agent, new ReplayOptions
        {
            LiveModelSteps = true,
            StepRules = { ["page_snapshot"] = new ReplayStepRule { IgnoreInputFields = new HashSet<string> { "reasoning" } } },
        });

        result.Outcome.Should().Be(ReplayOutcome.Passed, result.Inconclusive?.Message);
    }

    // VEV-37: the call site can say a step is a model call before it runs.

    [Fact]
    public async Task LiveReplay_RunsAModelStepTheRecordingNeverContained_WhenTheCallSiteSaysLlm()
    {
        var extracted = false;
        var result = await new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(BriefingTrace(), async ctx =>
        {
            var briefing = await BriefingAgent(Prompt)(ctx);
            await ctx.TrackStepAsync("extraction", briefing, () => { extracted = true; return Task.FromResult("Dentist"); },
                new StepOptions { Type = StepType.Llm });
            return briefing;
        }, new ReplayOptions { LiveModelSteps = true, Assertions = [TraceAssert.OutputContains("OVERDUE")] });

        result.Outcome.Should().Be(ReplayOutcome.Passed, result.Inconclusive?.Message);
        extracted.Should().BeTrue("a model step runs live in a live replay");
        result.Fidelity!.Bindings.Last().Should().Match<ReplayBinding>(b =>
            b.StepName == "extraction" && b.Kind == ReplayBindingKind.Live && b.RecordedIndex == null);
        result.ReplayedContext!.Steps.Last().Type.Should().Be("llm");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tool")]
    public async Task LiveReplay_StillStopsOnAnUnrecordedStepThatIsntMarkedLlm(string? type)
    {
        var ran = false;
        var result = await new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(BriefingTrace(), async ctx =>
        {
            await BriefingAgent(Prompt)(ctx);
            return await ctx.TrackStepAsync("charge_card", 42, () => { ran = true; return Task.FromResult("ok"); },
                new StepOptions { Type = type });
        }, new ReplayOptions { LiveModelSteps = true });

        ran.Should().BeFalse();
        result.Inconclusive!.Should().Match<ReplayStop>(s => s.StepName == "charge_card" && s.Reason == ReplayStopReason.NoRecording);
    }

    [Fact]
    public async Task LiveReplay_TheRecordedTypeWinsOverTheCallSite_AndTheConflictIsReported()
    {
        var portalVisits = 0;
        var result = await new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(BriefingTrace(), async ctx =>
        {
            // A mislabelled tool call: the recording knows visit_portal is a tool, so it is still served, not run.
            var items = await ctx.TrackStepAsync("visit_portal", "today", () => { portalVisits++; return Task.FromResult("live"); },
                new StepOptions { Type = StepType.Llm });
            return await ctx.TrackStepAsync("write_briefing", Prompt, () => Task.FromResult(FakeModel(Prompt, items)));
        }, new ReplayOptions { LiveModelSteps = true });

        portalVisits.Should().Be(0);
        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        var portal = result.Fidelity!.Bindings.First();
        portal.Kind.Should().Be(ReplayBindingKind.Matched);
        portal.DeclaredType.Should().Be("llm");
        result.Fidelity.Bindings.Last().DeclaredType.Should().BeNull("no type was declared at that call site");
    }

    [Fact]
    public async Task MockedReplay_StillRefusesAnUnrecordedModelStep()
    {
        var called = false;
        var result = await new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(BriefingTrace(), async ctx =>
        {
            await BriefingAgent(Prompt)(ctx);
            return await ctx.TrackStepAsync("extraction", "x", () => { called = true; return Task.FromResult("y"); },
                new StepOptions { Type = StepType.Llm });
        }, new ReplayOptions { MockLlmResponses = true });

        called.Should().BeFalse("mocked replay never makes a model call");
        result.Failures.Should().ContainSingle(f => f.Contains("no mock output for step 'extraction'"));
    }

    [Fact]
    public async Task ARecordedRun_CarriesTheCallSiteType_AndSetMetaStillOverridesIt()
    {
        var ctx = new VevalExecutionContext("tr_x", null);
        await ctx.TrackStepAsync("extraction", "x", () => Task.FromResult("y"), new StepOptions { Type = StepType.Llm });
        await ctx.TrackStepAsync("lookup", "x", h => { h.SetMeta("type", "tool"); return Task.FromResult("y"); },
            new StepOptions { Type = StepType.Llm });
        await ctx.TrackStepAsync("plain", "x", () => Task.FromResult("y"));

        ctx.Steps.Select(s => s.Type).Should().Equal("llm", "tool", "custom");
    }

    [Fact]
    public async Task LiveReplay_RunsAStepRecordedUntyped_WhenTheCallSiteSaysLlm()
    {
        // An older trace, or a model call that threw before SetMeta ran: the model step was saved as custom.
        var trace = BriefingTrace();
        trace.Steps[1].Type = "custom";
        var modelCalls = 0;

        var result = await new VevalSdk(new VevalOptions { ApiKey = "test" }).ReplayAsync(trace, async ctx =>
        {
            var items = await ctx.TrackStepAsync("visit_portal", "today", () => Task.FromResult("live"));
            return await ctx.TrackStepAsync("write_briefing", RegressedPrompt,
                () => { modelCalls++; return Task.FromResult(FakeModel(RegressedPrompt, items)); },
                new StepOptions { Type = StepType.Llm });
        }, new ReplayOptions { LiveModelSteps = true, Assertions = [TraceAssert.OutputContains("OVERDUE")] });

        modelCalls.Should().Be(1, "custom says nothing about the step, so the call site's llm applies");
        result.Outcome.Should().Be(ReplayOutcome.Failed, "the regressed prompt ran live and lost the overdue-first rule");
        result.Fidelity!.Bindings.Last().Should().Match<ReplayBinding>(b => b.Kind == ReplayBindingKind.Live && b.DeclaredType == null);
    }
}
