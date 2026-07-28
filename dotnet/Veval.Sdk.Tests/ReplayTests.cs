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
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" });
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
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" });
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
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" });
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
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" });
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
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" });
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
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" });
        var trace = CreateSampleTrace();
        var result = await sdk.ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("other-step", null, () => Task.FromResult("ok"));
            return "done";
        }, new ReplayOptions { Assertions = new ITraceAssertion[] { TraceAssert.ToolCalled("charge-card") } });
        result.Passed.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("charge-card"));
    }
}
