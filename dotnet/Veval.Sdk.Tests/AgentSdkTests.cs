using FluentAssertions;
using Veval.Sdk;

namespace Veval.Sdk.Tests;

public class AgentSdkTests
{
    [Fact]
    public async Task RunAsync_ReturnsCallbackResult()
    {
        var sdk = new VevalSdk(new VevalOptions
        {
            ApiKey = "sk_live_test",
            Endpoint = "http://localhost:0",
        });

        var result = await sdk.RunAsync("test-agent", async ctx =>
        {
            await ctx.TrackStepAsync("test-step", null, () => Task.FromResult("done"));
            return "hello";
        });

        result.Should().Be("hello");
    }

    [Fact]
    public async Task RunAsync_CapturesSteps()
    {
        var sdk = new VevalSdk(new VevalOptions
        {
            ApiKey = "sk_live_test",
            Endpoint = "http://localhost:0",
        });

        VevalExecutionContext? captured = null;
        await sdk.RunAsync("test-agent", async ctx =>
        {
            captured = ctx;
            await ctx.TrackStepAsync("step-1", "input-1", () => Task.FromResult("result-1"));
            await ctx.TrackStepAsync("step-2", "input-2", () => Task.FromResult("result-2"));
            return "done";
        });

        captured.Should().NotBeNull();
        captured!.Steps.Should().HaveCount(2);
        captured.Steps[0].Output.Should().Be("result-1");
        captured.Steps[1].Output.Should().Be("result-2");
    }

    [Fact]
    public async Task RunAsync_OnException_StillSendsTrace()
    {
        var sdk = new VevalSdk(new VevalOptions
        {
            ApiKey = "sk_live_test",
            Endpoint = "http://localhost:0",
        });

        var act = () => sdk.RunAsync<string>("test-agent", async ctx =>
        {
            throw new InvalidOperationException("boom");
        });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task TrackStepAsync_CapturesStepMeta()
    {
        var sdk = new VevalSdk(new VevalOptions
        {
            ApiKey = "sk_live_test",
            Endpoint = "http://localhost:0",
        });

        VevalExecutionContext? captured = null;
        await sdk.RunAsync("test-agent", async ctx =>
        {
            captured = ctx;
            await ctx.TrackStepAsync("llm-step", "prompt", async (step) =>
            {
                step.SetMeta("model", "claude-opus-4-6");
                step.SetMeta("tokens_in", 100);
                step.SetMeta("tokens_out", 50);
                step.SetMeta("cost_usd", 0.001m);
                return await Task.FromResult("response");
            });
            return "done";
        });

        var s = captured!.Steps[0];
        s.Model.Should().Be("claude-opus-4-6");
        s.TokensIn.Should().Be(100);
        s.TokensOut.Should().Be(50);
        s.CostUsd.Should().Be(0.001m);
    }
}
