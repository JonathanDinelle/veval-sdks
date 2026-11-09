using System.Net;
using FluentAssertions;
using Veval.Sdk;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

public class FaultInjectionTests
{
    private static StepData Recorded(string name, string type, object? input, object? output) => new()
    {
        StepId = Guid.NewGuid().ToString(), Name = name, Type = type, Input = input, Output = output, Status = "success",
        StartedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow, DurationMs = 10,
    };

    /// <summary>A recorded run: one flight search, then a model call that summarizes it.</summary>
    private static TraceData Trace() => new()
    {
        TraceId = "tr_faults",
        AgentName = "travel",
        Input = "Paris",
        Output = "Found AF123",
        Status = "success",
        Steps = new List<StepData>
        {
            Recorded("search_flights", "tool", "Paris", "[\"AF123\",\"BA456\"]"),
            Recorded("summarize", "llm", "flights: [\"AF123\",\"BA456\"]", "Found AF123"),
        },
    };

    private static VevalSdk Sdk() => new(new VevalOptions { ApiKey = "test" });

    /// <summary>The agent under test: retries the search up to <paramref name="retries"/> times, then summarizes.</summary>
    private static Func<VevalExecutionContext, Task<string>> Agent(int retries) => async ctx =>
    {
        string flights;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                flights = await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live"));
                break;
            }
            catch (Exception) when (attempt < retries) { }
        }
        return await ctx.TrackStepAsync("summarize", $"flights: {flights}", () => Task.FromResult("live"));
    };

    private static ReplayOptions Mocked(params ReplayFault[] faults) =>
        new() { MockLlmResponses = true, Faults = faults.ToList() };

    [Fact]
    public async Task Timeout_FirstCall_RetryRecovers_Passes()
    {
        var options = Mocked(ReplayFault.Timeout("search_flights").Times(1));
        options.Assertions = new[] { TraceAssert.MaxAttempts("search_flights", 1), TraceAssert.NoErrors() };

        var result = await Sdk().ReplayAsync(Trace(), Agent(retries: 2), options);

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        result.Output.Should().Be("Found AF123");
        result.Faults.Should().ContainSingle().Which.Fired.Should().Be(1);
        result.Fidelity!.Faulted.Should().Be(1);
        result.Fidelity.Unused.Should().BeEmpty();
        var faulted = result.ReplayedContext!.Steps[0];
        faulted.Status.Should().Be("error");
        faulted.Metadata["_source"].Should().Be("fault");
        faulted.Type.Should().Be("tool");
        result.Summary.Should().Contain("1 fault injected");
    }

    [Fact]
    public async Task Timeout_WithoutRetryPolicy_Fails()
    {
        var result = await Sdk().ReplayAsync(Trace(), Agent(retries: 0), Mocked(ReplayFault.Timeout("search_flights").Times(1)));

        result.Outcome.Should().Be(ReplayOutcome.Failed);
        result.Failures.Should().Contain(f => f.Contains("Replay threw exception") && f.Contains("Injected fault"));
    }

    [Fact]
    public async Task Timeout_ThrowsHttpClientStyleTimeout()
    {
        Exception? caught = null;
        await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            try { await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (Exception ex) { caught = ex; }
            return "";
        }, Mocked(ReplayFault.Timeout("search_flights")));

        caught.Should().BeOfType<TaskCanceledException>().Which.InnerException.Should().BeOfType<TimeoutException>();
    }

    [Fact]
    public async Task LimitedFault_AgentSwallowsErrorWithoutRetry_FailsOnUnusedRecording()
    {
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            try { return await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (Exception) { return "Sorry, try later"; }
        }, Mocked(ReplayFault.Timeout("search_flights").Times(1)));

        result.Outcome.Should().Be(ReplayOutcome.Failed);
        result.Failures.Should().Contain(f => f.Contains("never called"));
    }

    [Fact]
    public async Task EveryCallFault_FallbackMessageWithoutFurtherSteps_Passes()
    {
        var options = Mocked(ReplayFault.HttpError("search_flights", 503));
        options.Assertions = new[] { TraceAssert.NoErrors() };

        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            try { return await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                return "Search is down, try later";
            }
        }, options);

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        result.Output.Should().Be("Search is down, try later");
        result.Fidelity!.Unused.Should().HaveCount(2, "unused recordings are still reported");
    }

    [Fact]
    public async Task EveryCallFault_FallbackToolNotInRecording_IsInconclusiveInMockedMode()
    {
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            try { return await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (FaultHttpException)
            {
                return await ctx.TrackStepAsync("search_trains", "Paris", () => Task.FromResult("live"),
                    new StepOptions { Type = StepType.Tool });
            }
        }, Mocked(ReplayFault.HttpError("search_flights", 500)));

        result.Outcome.Should().Be(ReplayOutcome.Inconclusive);
        result.Passed.Should().BeFalse();
        result.Inconclusive!.StepName.Should().Be("search_trains");
        result.Inconclusive.Reason.Should().Be(ReplayStopReason.AfterFault);
        result.Inconclusive.Message.Should().StartWith("Replay stopped at step 2 ('search_trains') after an injected fault (HTTP 500 on 'search_flights')");
        result.Status.Should().Be("inconclusive");
    }

    [Fact]
    public async Task AfterFault_ModelCallWhoseInputCarriesTheError_IsInconclusiveInMockedMode()
    {
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            string flights;
            try { flights = await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (Exception ex) { flights = $"error: {ex.Message}"; }
            return await ctx.TrackStepAsync("summarize", $"flights: {flights}", () => Task.FromResult("live"));
        }, Mocked(ReplayFault.Timeout("search_flights")));

        result.Outcome.Should().Be(ReplayOutcome.Inconclusive);
        result.Inconclusive!.StepName.Should().Be("summarize");
        result.Inconclusive.Reason.Should().Be(ReplayStopReason.AfterFault);
        result.Inconclusive.Message.Should().Contain("its input matches no unused recording of 'summarize'");
    }

    [Fact]
    public async Task LiveReplay_ModelStepAfterFault_RunsLive()
    {
        string? modelSaw = null;
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            string flights;
            try { flights = await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (Exception) { flights = "unavailable"; }
            return await ctx.TrackStepAsync("summarize", $"flights: {flights}", () =>
            {
                modelSaw = flights;
                return Task.FromResult("No flights right now");
            });
        }, new ReplayOptions { LiveModelSteps = true, Faults = { ReplayFault.Timeout("search_flights") } });

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        modelSaw.Should().Be("unavailable");
        result.Output.Should().Be("No flights right now");
    }

    [Fact]
    public async Task RateLimit_CarriesStatusAndRetryAfter()
    {
        FaultHttpException? caught = null;
        await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            try { await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (FaultHttpException ex) { caught = ex; }
            return "";
        }, Mocked(ReplayFault.RateLimit("search_flights", TimeSpan.FromSeconds(2))));

        caught!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        caught.Status.Should().Be(429);
        caught.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
        caught.Headers["Retry-After"].Should().Be("2");
    }

    [Fact]
    public async Task CustomException_IsThrown()
    {
        Exception? caught = null;
        await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            try { await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (Exception ex) { caught = ex; }
            return "";
        }, Mocked(ReplayFault.Throw("search_flights", () => new InvalidDataException("vendor down"))));

        caught.Should().BeOfType<InvalidDataException>().Which.Message.Should().Be("vendor down");
    }

    [Fact]
    public async Task Malformed_Truncated_ReturnsHalfTheRecordedJson_ThenGuardRetries()
    {
        var outputs = new List<string>();
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            while (true)
            {
                var raw = await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live"));
                outputs.Add(raw);
                try { System.Text.Json.JsonDocument.Parse(raw).Dispose(); }
                catch (System.Text.Json.JsonException) { continue; }
                return await ctx.TrackStepAsync("summarize", $"flights: {raw}", () => Task.FromResult("live"));
            }
        }, new ReplayOptions
        {
            MockLlmResponses = true,
            Faults = { ReplayFault.Malformed("search_flights", MalformedOutput.Truncated).Times(1) },
            Assertions = new[] { TraceAssert.MaxAttempts("search_flights", 1), TraceAssert.NoErrors() },
        });

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        outputs[0].Should().Be("[\"AF123\"");
        outputs[1].Should().Be("[\"AF123\",\"BA456\"]");
    }

    [Fact]
    public async Task Malformed_EmptyAndReturns()
    {
        var seen = new List<object?>();
        await Sdk().ReplayAsync(Trace(), async ctx =>
        {
            seen.Add(await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")));
            seen.Add(await ctx.TrackStepAsync<object>("search_flights", "Paris", () => Task.FromResult<object>("live")));
            seen.Add(await ctx.TrackStepAsync<object>("search_flights", "Paris", () => Task.FromResult<object>("live")));
            return "";
        }, Mocked(
            ReplayFault.Malformed("search_flights", MalformedOutput.Empty).Times(1),
            ReplayFault.Malformed("search_flights", MalformedOutput.Empty).OnCall(2).Times(1),
            ReplayFault.Returns("search_flights", 42).Times(1)));

        seen[0].Should().Be("");
        seen[1].Should().Be("", "an object step whose recording holds text is a text step, as in Node and Python");
        seen[2].Should().Be(42);
    }

    [Fact]
    public async Task Malformed_EmptyOnATypedStep_IsTheDefault()
    {
        var trace = Trace();
        trace.Steps[0].Output = new List<string> { "AF123" };
        List<string>? seen = new();
        await Sdk().ReplayAsync(trace, async ctx =>
        {
            seen = await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult(new List<string>()));
            return "";
        }, Mocked(ReplayFault.Malformed("search_flights", MalformedOutput.Empty)));

        seen.Should().BeNull();
    }

    [Fact]
    public async Task Returns_ValueOfWrongType_ExplainsTheConversion()
    {
        var result = await Sdk().ReplayAsync(Trace(), async ctx =>
            await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult(new List<string>())),
            Mocked(ReplayFault.Returns("search_flights", 42)));

        result.Failures.Should().Contain(f => f.Contains("doesn't convert to List`1"));
    }

    [Fact]
    public async Task EveryCallFault_StillFailsOnRecordingsBeforeTheFaultedStep()
    {
        var trace = Trace();
        trace.Steps.Insert(0, Recorded("load_profile", "Paris", "frequent flyer", "tool"));

        // A regression drops load_profile; the fault on search_flights must not excuse it.
        var result = await Sdk().ReplayAsync(trace, async ctx =>
        {
            try { return await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (FaultHttpException) { return "Search is down"; }
        }, Mocked(ReplayFault.HttpError("search_flights", 503)));

        result.Outcome.Should().Be(ReplayOutcome.Failed);
        result.Failures.Should().ContainSingle().Which.Should()
            .Contain("never called 1 recorded step(s)").And.Contain("'load_profile' (recorded step 1)");
    }

    [Fact]
    public async Task UnfiredFault_FailsTheReplay()
    {
        var result = await Sdk().ReplayAsync(Trace(), Agent(retries: 0), Mocked(ReplayFault.Timeout("book_hotel")));

        result.Outcome.Should().Be(ReplayOutcome.Failed);
        result.Failures.Should().ContainSingle().Which.Should()
            .StartWith("Fault not triggered: Timeout on 'book_hotel' never fired: the agent never called 'book_hotel'");
        result.Faults.Single().Triggered.Should().BeFalse();
    }

    [Fact]
    public async Task WhenInput_FaultsOnlyMatchingCalls()
    {
        var result = await Sdk().ReplayAsync(Trace(), Agent(retries: 0),
            Mocked(ReplayFault.Timeout("search_flights").WhenInput("Rome")));

        result.Failures.Should().ContainSingle().Which.Should()
            .Contain("none of the 1 call(s) of 'search_flights' matched it");
    }

    [Fact]
    public async Task OnCall_SkipsEarlierCalls()
    {
        var trace = Trace();
        trace.Steps.Insert(1, Recorded("search_flights", "tool", "Paris", "[\"AF123\",\"BA456\"]"));
        var calls = 0;
        var result = await Sdk().ReplayAsync(trace, async ctx =>
        {
            await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live"));
            calls++;
            try { await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (TaskCanceledException) { await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            calls++;
            return await ctx.TrackStepAsync("summarize", "flights: [\"AF123\",\"BA456\"]", () => Task.FromResult("live"));
        }, Mocked(ReplayFault.Timeout("search_flights").OnCall(2).Times(1)));

        result.Outcome.Should().Be(ReplayOutcome.Passed, string.Join("; ", result.Failures));
        result.ReplayedContext!.Steps.Select(s => s.Status).Should()
            .Equal("success", "error", "success", "success");
    }

    [Fact]
    public async Task MaxAttempts_TooManyRetries_Fails()
    {
        var options = Mocked(ReplayFault.Timeout("search_flights").Times(3));
        options.Assertions = new[] { TraceAssert.MaxAttempts("search_flights", 2) };

        var result = await Sdk().ReplayAsync(Trace(), Agent(retries: 5), options);

        result.Failures.Should().ContainSingle().Which.Should()
            .Be("MaxAttempts: expected at most 2 call(s) of 'search_flights' after its first failure, got 3");
    }

    [Fact]
    public async Task MaxAttempts_StepNeverFailed_Fails()
    {
        var result = await Sdk().ReplayAsync(Trace(), Agent(retries: 0), new ReplayOptions
        {
            MockLlmResponses = true,
            Assertions = new[] { TraceAssert.MaxAttempts("search_flights", 2) },
        });

        result.Failures.Should().ContainSingle().Which.Should().Contain("never failed");
    }

    [Fact]
    public async Task Faults_WithoutMockedOrLiveReplay_Throw()
    {
        var act = () => Sdk().ReplayAsync(Trace(), Agent(retries: 0),
            new ReplayOptions { Faults = { ReplayFault.Timeout("search_flights") } });

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*mocked or live replay*");
    }

    [Fact]
    public void Description_NamesKindStepAndScope()
    {
        ReplayFault.HttpError("book", 503).Times(2).Description.Should().Be("HTTP 503 on 'book' (first 2 calls)");
        ReplayFault.RateLimit("llm").Description.Should().Be("Rate limit (429, Retry-After 1s) on 'llm'");
        ReplayFault.Returns("parse", 42).Description.Should().Be("Returned value on 'parse'");
        FluentActions.Invoking(() => ReplayFault.HttpError("book", 200)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // --- Faults in scenarios ---------------------------------------------------------------------------------------

    private static VevalTestSdk TestSdk() => new VevalTestSdk(new VevalOptions { ApiKey = "test" }).WithReplay(Trace());

    [Fact]
    public async Task Scenario_FaultsApplyToTraceBackedItems_AndAreReported()
    {
        var result = await TestSdk().RunScenarioAsync("travel", Agent(retries: 1), [TraceAssert.MaxAttempts("search_flights", 1)],
            [new ScenarioItem { Name = "recovers", TraceId = "tr_faults" }],
            new ScenarioOptions { Faults = { ReplayFault.Timeout("search_flights").Times(1) } });

        result.Passed.Should().BeTrue(result.Summary);
        var item = result.Results.Single();
        item.Faults.Should().ContainSingle().Which.Fired.Should().Be(1);

        var json = System.Text.Json.JsonSerializer.SerializeToElement(Internal.ScenarioReport.RunPayload(result, "synthetic"));
        var fault = json.GetProperty("results")[0].GetProperty("faults")[0];
        fault.GetProperty("description").GetString().Should().Be("Timeout on 'search_flights' (first call)");
        fault.GetProperty("step_name").GetString().Should().Be("search_flights");
        fault.GetProperty("fired").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Scenario_ItemFaultsAddToScenarioFaults()
    {
        var result = await TestSdk().RunScenarioAsync("travel", Agent(retries: 2), [],
            [new ScenarioItem { TraceId = "tr_faults", Faults = { ReplayFault.HttpError("search_flights", 503).Times(1) } }],
            new ScenarioOptions { Faults = { ReplayFault.Timeout("search_flights").Times(1) } });

        result.Passed.Should().BeTrue(result.Summary);
        result.Results.Single().Faults.Select(f => f.Fired).Should().Equal(1, 1);
    }

    [Fact]
    public async Task Scenario_InconclusiveAfterFault_FailsTheRunWhateverTheAllowance()
    {
        var result = await TestSdk().RunScenarioAsync("travel", async ctx =>
        {
            try { return await ctx.TrackStepAsync("search_flights", "Paris", () => Task.FromResult("live")); }
            catch (FaultHttpException)
            {
                return await ctx.TrackStepAsync("search_trains", "Paris", () => Task.FromResult("live"),
                    new StepOptions { Type = StepType.Tool });
            }
        }, [],
            [new ScenarioItem { Name = "fallback", TraceId = "tr_faults", Faults = { ReplayFault.HttpError("search_flights", 500) } }],
            new ScenarioOptions { MaxInconclusiveRate = 1 });

        result.Passed.Should().BeFalse();
        result.InconclusiveCount.Should().Be(1);
        result.InconclusiveAfterFaultCount.Should().Be(1);
        result.InconclusiveRate.Should().Be(0);
        result.StayedOnPathRate.Should().BeNull();
        result.Results.Single().InconclusiveAfterFault.Should().BeTrue();
        result.Summary.Should().Be("failed: inconclusive after a fault — 0 passed, 0 failed, 0 inconclusive, 1 inconclusive after a fault");

        var json = System.Text.Json.JsonSerializer.SerializeToElement(Internal.ScenarioReport.RunPayload(result, "synthetic"));
        json.GetProperty("inconclusive_count").GetInt32().Should().Be(1);
        json.GetProperty("inconclusive_after_fault_count").GetInt32().Should().Be(1);
        json.GetProperty("results")[0].GetProperty("inconclusive").GetProperty("reason").GetString().Should().Be("after_fault");
    }

    [Fact]
    public async Task Scenario_SyntheticItemWithFaults_Fails()
    {
        var result = await TestSdk().RunScenarioAsync("travel", Agent(retries: 0), [],
            [new ScenarioItem { Name = "synthetic", Input = "Paris", Faults = { ReplayFault.Timeout("search_flights") } }]);

        result.Results.Single().Failures.Should().ContainSingle().Which.Should().Contain("needs a trace-backed item");
    }
}
