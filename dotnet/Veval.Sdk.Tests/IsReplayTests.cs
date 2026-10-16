using FluentAssertions;
using Veval.Sdk;

namespace Veval.Sdk.Tests;

/// <summary><see cref="VevalExecutionContext.IsReplay"/>. Mirrors sdk/node/test/is-replay.test.ts.</summary>
public class IsReplayTests
{
    private static StepData Recorded(string name, string type, object? input, object? output) =>
        new() { StepId = name, Name = name, Type = type, Input = input, Output = output, Status = "success", StartedAt = DateTime.UtcNow };

    private static TraceData BookingTrace() => new()
    {
        TraceId = "tr_booking",
        AgentName = "booking",
        Input = "Friday",
        Status = "success",
        Steps =
        [
            Recorded("find_slot", "tool", "Friday", "10:00"),
            Recorded("write_reply", "llm", "10:00", "Booked for 10:00."),
        ],
    };

    private static Func<VevalExecutionContext, Task<string>> IsReplayOf(List<bool> seen) => async ctx =>
    {
        seen.Add(ctx.IsReplay);
        var slot = await ctx.TrackStepAsync("find_slot", ctx.Input, () => Task.FromResult("11:30"));
        return await ctx.TrackStepAsync("write_reply", slot, () => Task.FromResult($"Booked for {slot}."));
    };

    // A closed local port: the traces these runs send fail fast and never reach the real API.
    private static VevalOptions Options() => new() { ApiKey = "test", Endpoint = "http://127.0.0.1:1" };
    private static VevalSdk Sdk() => new(Options());
    private static VevalTestSdk TestSdk() => new(Options());

    [Fact]
    public async Task IsFalse_InANormalRun()
    {
        var seen = new List<bool>();
        await Sdk().RunAsync("booking", IsReplayOf(seen), "Friday");
        seen.Should().Equal(false);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task IsTrue_InEveryReplayMode(bool mocked, bool live)
    {
        var seen = new List<bool>();
        await Sdk().ReplayAsync(BookingTrace(), IsReplayOf(seen), new ReplayOptions { MockLlmResponses = mocked, LiveModelSteps = live });
        seen.Should().Equal(true);
    }

    [Fact]
    public async Task IsTrue_UnderTheTestSdkWithReplay()
    {
        var seen = new List<bool>();
        await TestSdk().WithReplay(BookingTrace()).RunAsync("booking", IsReplayOf(seen), "Friday");
        seen.Should().Equal(true);
    }

    [Fact]
    public async Task LetsALiveReplaySkipASideEffectOutsideAnyStep()
    {
        var sentEmails = new List<string>();
        async Task<string> Agent(VevalExecutionContext ctx)
        {
            var reply = await ctx.TrackStepAsync("write_reply", "10:00", () => Task.FromResult("Booked for 10:00."));
            if (!ctx.IsReplay) sentEmails.Add(reply);
            return reply;
        }
        var trace = BookingTrace();
        trace.Steps.RemoveAt(0);

        await Sdk().RunAsync("booking", Agent, "Friday");
        var result = await Sdk().ReplayAsync(trace, Agent, new ReplayOptions { LiveModelSteps = true });

        result.Passed.Should().BeTrue(string.Join("; ", result.Failures));
        sentEmails.Should().Equal("Booked for 10:00.");
    }

    [Fact]
    public async Task MarksTraceBackedScenarioItems_ButNotSyntheticOnes()
    {
        var seen = new List<bool>();
        await TestSdk().WithReplay(BookingTrace()).RunScenarioAsync("booking", IsReplayOf(seen), [],
        [
            new ScenarioItem { Name = "recorded", TraceId = "tr_booking" },
            new ScenarioItem { Name = "synthetic", Input = "Monday" },
        ]);
        seen.Should().Equal(true, false);
    }
}
