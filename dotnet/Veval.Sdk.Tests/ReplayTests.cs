using System.Text.Json;
using FluentAssertions;
using Veval.Sdk;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

public class ReplayTests
{
    private TraceData CreateSampleTrace()
    {
        return new TraceData
        {
            TraceId = "tr_test",
            AgentName = "test-agent",
            Input = "hello",
            Output = "world",
            Status = "success",
            Steps = new List<StepData>
            {
                new() { StepId = "s1", Name = "classify", Type = "llm", Input = "hello", Output = "greeting", Status = "success", StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow, DurationMs = 100 },
                new() { StepId = "s2", Name = "respond",  Type = "llm", Input = "greeting", Output = "world", Status = "success", StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow, DurationMs = 200 },
            },
        };
    }

    [Fact]
    public async Task ReplayAsync_WithMocking_ReturnsRecordedOutputs()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = CreateSampleTrace();
        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            var category = await ctx.TrackStepAsync("classify", trace.Input, () => Task.FromResult(""));
            var answer   = await ctx.TrackStepAsync("respond",  category,    () => Task.FromResult(""));
            return answer?.ToString() ?? "";
        }, new ReplayOptions
        {
            MockLlmResponses = true,
            Assertions = new ITraceAssertion[] { TraceAssert.MaxSteps(5), TraceAssert.NoErrors() },
        });
        result.Passed.Should().BeTrue();
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayAsync_ExceedsMaxSteps_Fails()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = CreateSampleTrace();
        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("s1", null, () => Task.FromResult("a"));
            await ctx.TrackStepAsync("s2", null, () => Task.FromResult("b"));
            await ctx.TrackStepAsync("s3", null, () => Task.FromResult("c"));
            return "done";
        }, new ReplayOptions { Assertions = new ITraceAssertion[] { TraceAssert.MaxSteps(2) } });
        result.Passed.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("MaxSteps"));
    }

    [Fact]
    public async Task ReplayAsync_WithError_DetectedByNoErrors()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = CreateSampleTrace();
        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("bad-step", null, () => Task.FromException<string>(new Exception("something broke")));
            return "partial";
        }, new ReplayOptions { Assertions = new ITraceAssertion[] { TraceAssert.NoErrors() } });
        result.Passed.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("error"));
    }

    [Fact]
    public async Task ToolCalled_WhenToolStepPresent_Passes()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = CreateSampleTrace();
        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("charge-card", null, (handle) =>
            {
                handle.SetMeta("type", "tool");
                return Task.FromResult("ok");
            });
            return "done";
        }, new ReplayOptions { Assertions = new ITraceAssertion[] { TraceAssert.ToolCalled("charge-card") } });
        result.Passed.Should().BeTrue();
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task ToolCalled_WhenStepExistsButWrongType_Fails()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = CreateSampleTrace();
        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            // step named "charge-card" but type defaults to "custom", not "tool"
            await ctx.TrackStepAsync("charge-card", null, () => Task.FromResult("ok"));
            return "done";
        }, new ReplayOptions { Assertions = new ITraceAssertion[] { TraceAssert.ToolCalled("charge-card") } });
        result.Passed.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("charge-card"));
    }

    [Fact]
    public async Task ToolCalled_WhenToolStepAbsent_Fails()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = CreateSampleTrace();
        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("other-step", null, () => Task.FromResult("ok"));
            return "done";
        }, new ReplayOptions { Assertions = new ITraceAssertion[] { TraceAssert.ToolCalled("charge-card") } });
        result.Passed.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("charge-card"));
    }

    private static StepData Recorded(string id, string name, object? input, object? output) =>
        new() { StepId = id, Name = name, Type = "tool", Input = input, Output = output, Status = "success", StartedAt = DateTime.UtcNow };

    private static TraceData TraceWith(params StepData[] steps) =>
        new() { TraceId = "tr_bind", AgentName = "test-agent", Input = "go", Status = "success", Steps = steps.ToList() };

    private static ReplayOptions Mocked() => new() { MockLlmResponses = true };

    [Fact]
    public async Task ReplayAsync_ReorderedCallsWithSameName_EachGetsItsOwnRecording()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = TraceWith(
            Recorded("s1", "visit_portal", "portal-a", "a-result"),
            Recorded("s2", "visit_portal", "portal-b", "b-result"));
        var outputs = new List<string>();

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            outputs.Add(await ctx.TrackStepAsync("visit_portal", "portal-b", () => Task.FromResult("live")));
            outputs.Add(await ctx.TrackStepAsync("visit_portal", "portal-a", () => Task.FromResult("live")));
            return "done";
        }, Mocked());

        result.Passed.Should().BeTrue();
        outputs.Should().Equal("b-result", "a-result");
        result.Fidelity!.IsFaithful.Should().BeTrue();
        result.Fidelity.Bindings.Select(b => b.RecordedIndex).Should().Equal(1, 0);
    }

    [Fact]
    public async Task ReplayAsync_SkippedRecordedStep_Fails()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = TraceWith(
            Recorded("s1", "check_gate", "go", "open"),
            Recorded("s2", "visit_portal", "portal-a", "a-result"));

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("check_gate", "go", () => Task.FromResult("live"));
            return "done";
        }, Mocked());

        result.Passed.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("never called") && f.Contains("'visit_portal' (recorded step 2)"));
        result.Fidelity!.Unused.Should().ContainSingle(u => u.StepName == "visit_portal" && u.RecordedIndex == 1);
    }

    [Fact]
    public async Task ReplayAsync_AddedCall_FailsInsteadOfCallingLive()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = TraceWith(Recorded("s1", "visit_portal", "portal-a", "a-result"));
        var liveCalls = 0;

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("visit_portal", "portal-a", () => { liveCalls++; return Task.FromResult("live"); });
            await ctx.TrackStepAsync("visit_portal", "portal-a", () => { liveCalls++; return Task.FromResult("live"); });
            return "done";
        }, Mocked());

        result.Passed.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("no mock output for step 'visit_portal'"));
        liveCalls.Should().Be(0);
    }

    [Fact]
    public async Task ReplayAsync_InputThatChangesEveryRun_IsServedInOrderAndReportedAsDiverged()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = TraceWith(
            Recorded("s1", "plan", "Today is 2026-10-01. Plan the day.", "plan-1"),
            Recorded("s2", "plan", "Today is 2026-10-01. Revise the plan.", "plan-2"));
        var outputs = new List<string>();

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            outputs.Add(await ctx.TrackStepAsync("plan", "Today is 2026-10-16. Plan the day.", () => Task.FromResult("live")));
            outputs.Add(await ctx.TrackStepAsync("plan", "Today is 2026-10-16. Revise the plan.", () => Task.FromResult("live")));
            return "done";
        }, Mocked());

        result.Passed.Should().BeTrue();
        outputs.Should().Equal("plan-1", "plan-2");
        result.Fidelity!.Diverged.Should().Be(2);
        result.Fidelity.IsFaithful.Should().BeFalse();
        var first = result.Fidelity.Bindings[0];
        first.Kind.Should().Be(ReplayBindingKind.Diverged);
        first.RecordedInput.Should().Be("Today is 2026-10-01. Plan the day.");
        first.ActualInput.Should().Be("Today is 2026-10-16. Plan the day.");
    }

    [Fact]
    public async Task ReplayAsync_DivergedInputWithCompareWithRecording_Fails()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = TraceWith(Recorded("s1", "plan", "old prompt", "plan-1"));

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("plan", "new prompt", () => Task.FromResult("live"));
            return "done";
        }, new ReplayOptions { MockLlmResponses = true, CompareWithRecording = new SnapshotOptions() });

        result.Passed.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("drifted from its recording"));
    }

    [Fact]
    public async Task ReplayAsync_RecordedInputFromApi_MatchesRegardlessOfKeyOrder()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = JsonSerializer.Deserialize<TraceData>("""
            {"trace_id":"tr_json","steps":[
              {"step_id":"s1","name":"visit_portal","input":{"portal":"b","page":2},"output":"b-result"},
              {"step_id":"s2","name":"visit_portal","input":{"portal":"a","page":1},"output":"a-result"}]}
            """)!;
        string? output = null;

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            output = await ctx.TrackStepAsync("visit_portal", new { page = 1, portal = "a" }, () => Task.FromResult("live"));
            await ctx.TrackStepAsync("visit_portal", new { page = 2, portal = "b" }, () => Task.FromResult("live"));
            return "done";
        }, Mocked());

        result.Passed.Should().BeTrue();
        output.Should().Be("a-result");
        result.Fidelity!.Matched.Should().Be(2);
    }

    private record Query(DateTime Day);

    [Fact]
    public async Task ReplayAsync_MatchesObjectInputsInTheFormTheyWereSent()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var day15 = new Query(new DateTime(2026, 10, 15));
        var day16 = new Query(new DateTime(2026, 10, 16));
        JsonElement AsSent(object v) => JsonSerializer.SerializeToElement(v);  // what the trace stores
        var trace = TraceWith(Recorded("s1", "fetch", AsSent(day15), "15th"), Recorded("s2", "fetch", AsSent(day16), "16th"));
        var outputs = new List<string>();

        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            outputs.Add(await ctx.TrackStepAsync("fetch", day16, () => Task.FromResult("live")));
            outputs.Add(await ctx.TrackStepAsync("fetch", day15, () => Task.FromResult("live")));
            return "done";
        }, Mocked());

        outputs.Should().Equal("16th", "15th");
        result.Fidelity!.Matched.Should().Be(2);
    }

    [Fact]
    public async Task ReplayAsync_AgentThrows_DoesNotAlsoReportUnusedRecordings()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var trace = TraceWith(
            Recorded("s1", "check_gate", "go", "open"),
            Recorded("s2", "visit_portal", "portal-a", "a-result"));

        var result = await sdk.ReplayAsync<string>(trace, async ctx =>
        {
            await ctx.TrackStepAsync("check_gate", "go", () => Task.FromResult("live"));
            throw new InvalidOperationException("boom");
        }, Mocked());

        result.Failures.Should().ContainSingle().Which.Should().Contain("boom");
        result.Fidelity!.Unused.Should().ContainSingle();
    }

    [Fact]
    public async Task WithReplay_RunAsync_ThrowsWhenARecordedStepIsNeverCalled()
    {
        var testSdk = new VevalTestSdk(new VevalOptions { ApiKey = "test" }).WithReplay(TraceWith(
            Recorded("s1", "check_gate", "go", "open"),
            Recorded("s2", "visit_portal", "portal-a", "a-result")));

        var run = () => testSdk.RunAsync("agent", ctx => ctx.TrackStepAsync("check_gate", "go", () => Task.FromResult("live")));

        (await run.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*never called*'visit_portal'*");
        testSdk.LastStatus.Should().Be("error");
    }

    [Fact]
    public async Task ReplayAsync_WithoutMocking_HasNoFidelity()
    {
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });
        var result = await sdk.ReplayAsync(CreateSampleTrace(), _ => Task.FromResult("done"), new ReplayOptions());
        result.Fidelity.Should().BeNull();
        result.Passed.Should().BeTrue();
    }
}
