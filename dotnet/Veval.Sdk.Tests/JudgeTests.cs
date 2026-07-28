using FluentAssertions;
using NSubstitute;
using Veval.Sdk;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

public class JudgeTests
{
    [Fact]
    public async Task Judge_WhenVevalReportsPassed_ReturnsNull()
    {
        var veval = Substitute.For<IVevalSdk>();
        veval.JudgeAsync("must be polite", Arg.Any<VevalExecutionContext>(), Arg.Any<JudgeOptions?>())
            .Returns(new JudgeResult { Passed = true, Score = 0.95, Reasoning = "Polite and on-topic." });

        var assertion = TraceAssert.Judge(veval, "must be polite");
        var ctx = new VevalExecutionContext("tr_test", "hi");

        var failure = await assertion.EvaluateAsync(ctx);

        failure.Should().BeNull();
    }

    [Fact]
    public async Task Judge_WhenVevalReportsFailed_ReturnsFailureWithScoreAndReasoning()
    {
        var veval = Substitute.For<IVevalSdk>();
        veval.JudgeAsync("must be concise", Arg.Any<VevalExecutionContext>(), Arg.Any<JudgeOptions?>())
            .Returns(new JudgeResult { Passed = false, Score = 0.3, Reasoning = "Answer rambles." });

        var assertion = TraceAssert.Judge(veval, "must be concise");
        var ctx = new VevalExecutionContext("tr_test", "hi");

        var failure = await assertion.EvaluateAsync(ctx);

        failure.Should().NotBeNull();
        failure.Should().Contain("must be concise");
        failure.Should().Contain("0.30");
        failure.Should().Contain("Answer rambles.");
    }

    [Fact]
    public async Task Judge_WhenJudgeCallThrows_FailsRatherThanPassingSilently()
    {
        var veval = Substitute.For<IVevalSdk>();
        veval.JudgeAsync(Arg.Any<string>(), Arg.Any<VevalExecutionContext>(), Arg.Any<JudgeOptions?>())
            .Returns<JudgeResult>(_ => throw new InvalidOperationException("Judge request failed with status 503"));

        var assertion = TraceAssert.Judge(veval, "must be accurate");
        var ctx = new VevalExecutionContext("tr_test", "hi");

        var failure = await assertion.EvaluateAsync(ctx);

        failure.Should().NotBeNull();
        failure.Should().Contain("evaluation failed");
        failure.Should().Contain("503");
    }

    [Fact]
    public async Task VevalTestSdk_WithJudgeMock_ReturnsMockedResultInsteadOfCallingLive()
    {
        var testSdk = new VevalTestSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" })
            .WithJudgeMock("must be polite", passed: true, score: 0.88, reasoning: "Mocked pass.");

        var ctx = new VevalExecutionContext("tr_test", "hi");
        var result = await testSdk.JudgeAsync("must be polite", ctx);

        result.Passed.Should().BeTrue();
        result.Score.Should().Be(0.88);
        result.Reasoning.Should().Be("Mocked pass.");
    }

    [Fact]
    public async Task VevalTestSdk_WithoutJudgeMock_ThrowsInsteadOfMakingLiveCall()
    {
        var testSdk = new VevalTestSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" });
        var ctx = new VevalExecutionContext("tr_test", "hi");

        var act = async () => await testSdk.JudgeAsync("unmocked criteria", ctx);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unmocked criteria*");
    }

    [Fact]
    public async Task VevalTestSdk_ReplayAsync_WithJudgeAssertionAndMock_Passes()
    {
        var testSdk = new VevalTestSdk(new VevalOptions { ApiKey = "test", Endpoint = "http://localhost:0" })
            .WithJudgeMock("must be helpful", passed: true, score: 1.0);

        var trace = new TraceData
        {
            TraceId = "tr_test",
            Input = "hello",
            Steps = new List<StepData>
            {
                new() { StepId = "s1", Name = "answer", Output = "world", Status = "success" },
            },
        };

        var result = await testSdk.ReplayAsync(trace, async ctx =>
        {
            var answer = await ctx.TrackStepAsync("answer", trace.Input, () => Task.FromResult(""));
            return answer;
        }, new ReplayOptions
        {
            MockLlmResponses = true,
            Assertions = new[] { TraceAssert.Judge(testSdk, "must be helpful") },
        });

        result.Passed.Should().BeTrue();
        result.Failures.Should().BeEmpty();
    }
}
