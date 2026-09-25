using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using Veval.Sdk.Assertions;

namespace Veval.Sdk.Tests;

public class SnapshotContentTests
{
    private static async Task<VevalExecutionContext> Run(params (string Name, object? Input, object? Output)[] steps)
    {
        var ctx = new VevalExecutionContext("tr_run", null);
        foreach (var (name, input, output) in steps)
            await ctx.TrackStepAsync(name, input, () => Task.FromResult(output));
        return ctx;
    }

    private static StepData Recorded(string name, object? input, object? output, string type = "llm") => new()
    {
        StepId = Guid.NewGuid().ToString(), Name = name, Type = type, Input = input, Output = output,
        Status = "success", StartedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task IdenticalRun_HasNoChanges()
    {
        var baseline = SnapshotData.FromContext(await Run(("classify", "q", "a"), ("respond", "a", "b")));
        var diff = SnapshotComparer.Compare(baseline, await Run(("classify", "q", "a"), ("respond", "a", "b")));

        diff.HasChanges.Should().BeFalse();
        diff.Summary().Should().Contain("no changes");
    }

    [Fact]
    public async Task ExtraCallOfSameStep_IsReportedAsAdded()
    {
        var baseline = SnapshotData.FromContext(await Run(("fetch", "x", "n"), ("write", "n", "b")));
        var diff = SnapshotComparer.Compare(baseline, await Run(("fetch", "x", "n"), ("write", "n", "b"), ("write", "n", "b")));

        diff.Changes.Should().ContainSingle(c => c.Kind == SnapshotChangeKind.Added && c.StepName == "write" && c.Occurrence == 2);
        diff.AddedSteps.Should().Equal("write");
    }

    [Fact]
    public async Task ChangedPrompt_IsReportedAsInputChangeWithLineDiff()
    {
        var baseline = SnapshotData.FromContext(await Run(("write", "System: overdue tasks first.\nData: ...", "b")));
        var diff = SnapshotComparer.Compare(baseline, await Run(("write", "System: tasks alphabetically.\nData: ...", "b")));

        var change = diff.Changes.Should().ContainSingle().Which;
        change.Kind.Should().Be(SnapshotChangeKind.InputChanged);
        change.Detail.Should().Contain("- System: overdue tasks first.").And.Contain("+ System: tasks alphabetically.");
        diff.Summary("briefing").Should().Contain("input changed for 'write'");
    }

    [Fact]
    public async Task InputsFromApiJson_CompareEqualToLiveObjects_RegardlessOfKeyOrder()
    {
        var live = await Run(("search", new { query = "weather", limit = 5 }, "r"));
        var fromApi = JsonSerializer.Deserialize<JsonElement>("""{ "limit": 5, "query": "weather" }""");
        var baseline = SnapshotData.FromTrace(new TraceData { TraceId = "tr", Steps = [Recorded("search", fromApi, "r")] });

        SnapshotComparer.Compare(baseline, live).HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task IgnoreFields_DropsVolatileValues()
    {
        var baseline = SnapshotData.FromContext(await Run(("call", new { prompt = "p", timestamp = "2026-01-01" }, "r")));
        var current = await Run(("call", new { prompt = "p", timestamp = "2026-09-24" }, "r"));

        SnapshotComparer.Compare(baseline, current).HasChanges.Should().BeTrue();
        SnapshotComparer.Compare(baseline, current, new SnapshotOptions { IgnoreFields = new HashSet<string> { "timestamp" } })
            .HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Outputs_AreComparedOnlyWhenEnabled()
    {
        var baseline = SnapshotData.FromContext(await Run(("tool", "in", "sunny")));
        var current = await Run(("tool", "in", "snow"));

        SnapshotComparer.Compare(baseline, current).HasChanges.Should().BeFalse();
        SnapshotComparer.Compare(baseline, current, new SnapshotOptions { CompareOutputs = true })
            .Changes.Should().ContainSingle(c => c.Kind == SnapshotChangeKind.OutputChanged);
    }

    [Fact]
    public async Task MovedStep_IsReportedOnceAsMoved()
    {
        var baseline = SnapshotData.FromContext(await Run(("a", 1, 1), ("b", 1, 1), ("c", 1, 1)));
        var diff = SnapshotComparer.Compare(baseline, await Run(("b", 1, 1), ("c", 1, 1), ("a", 1, 1)));

        diff.Changes.Should().ContainSingle(c => c.Kind == SnapshotChangeKind.Moved && c.StepName == "a");
        diff.OrderChanges.Should().ContainSingle();
    }

    [Fact]
    public async Task LegacySnapshot_ComparesSequenceOnly_AndSaysSo()
    {
        var legacy = new SnapshotData { StepNames = ["write"], StepOrder = ["write"], StepCount = 1 };
        var diff = SnapshotComparer.Compare(legacy, await Run(("write", "any input", "b"), ("write", "x", "y")));

        diff.ComparedContent.Should().BeFalse();
        diff.Changes.Should().ContainSingle(c => c.Kind == SnapshotChangeKind.Added);
        diff.Summary().Should().Contain("legacy snapshot");
    }

    [Fact]
    public async Task Replay_WithCompareWithRecording_CatchesOutputBoundToWrongCall()
    {
        // Recorded: the same step used twice — classify, then answer.
        var trace = new TraceData
        {
            TraceId = "tr_recorded",
            Steps = [Recorded("call-claude", "Classify: ...", "category: urgent"), Recorded("call-claude", "Answer: ...", "Here is your briefing.")],
        };
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });

        // New code skips classification, so its single call is served the *classification* output.
        var result = await sdk.ReplayAsync(trace,
            ctx => ctx.TrackStepAsync("call-claude", "Answer: ...", () => Task.FromResult("live")),
            new ReplayOptions { MockLlmResponses = true, CompareWithRecording = new SnapshotOptions() });

        result.Output.Should().Be("category: urgent", "replay binds recorded outputs by step name");
        result.Passed.Should().BeFalse();
        result.RecordingDiff!.Changes.Should().Contain(c => c.Kind == SnapshotChangeKind.InputChanged)
            .And.Contain(c => c.Kind == SnapshotChangeKind.Removed);
        result.Failures.Should().ContainSingle(f => f.Contains("drifted from its recording"));
    }

    [Fact]
    public async Task Replay_KeepsRecordedStepType_SoToolCalledPasses()
    {
        var trace = new TraceData { TraceId = "tr", Steps = [Recorded("fetch-weather", "2026-09-24", "sunny", type: "tool")] };
        var sdk = new VevalSdk(new VevalOptions { ApiKey = "test" });

        var result = await sdk.ReplayAsync(trace,
            ctx => ctx.TrackStepAsync("fetch-weather", "2026-09-24", () => Task.FromResult("live")),
            new ReplayOptions { MockLlmResponses = true, Assertions = [TraceAssert.ToolCalled("fetch-weather")], CompareWithRecording = new SnapshotOptions() });

        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task MatchesSnapshot_FailsWithDiff_WhenInputChanged()
    {
        var baseline = SnapshotData.FromContext(await Run(("write", "overdue first", "b")));
        var failure = await TraceAssert.MatchesSnapshot(baseline).EvaluateAsync(await Run(("write", "alphabetical", "b")));

        failure.Should().StartWith("MatchesSnapshot:").And.Contain("- overdue first").And.Contain("+ alphabetical");
    }

    [Fact]
    public async Task MatchesSnapshot_ByName_UsesTestSdkBaseline_AndFailsWhenMissing()
    {
        var baseline = SnapshotData.FromContext(await Run(("write", "p", "b")));
        var sdk = new VevalTestSdk(new VevalOptions { ApiKey = "test" }).WithSnapshot("briefing", baseline);
        var run = await Run(("write", "p", "b"));

        (await TraceAssert.MatchesSnapshot(sdk, "briefing").EvaluateAsync(run)).Should().BeNull();

        var missing = Substitute.For<IVevalSdk>();
        missing.GetSnapshotAsync("nope").Returns((SnapshotData?)null);
        (await TraceAssert.MatchesSnapshot(missing, "nope").EvaluateAsync(run)).Should().Contain("no snapshot named 'nope'");
    }

    [Fact]
    public async Task MatchesSnapshot_FailsLoudly_WhenBaselineCannotBeLoaded()
    {
        var broken = Substitute.For<IVevalSdk>();
        broken.GetSnapshotAsync("briefing").Returns<SnapshotData?>(_ => throw new HttpRequestException("connection refused"));

        var failure = await TraceAssert.MatchesSnapshot(broken, "briefing").EvaluateAsync(await Run(("write", "p", "b")));

        failure.Should().Contain("could not load snapshot 'briefing'").And.Contain("connection refused");
    }

    [Fact]
    public async Task PromptInsideObjectInput_DiffsLineByLine()
    {
        var before = new { model = "claude-opus-5", system = "You are helpful.\n1. Overdue tasks first.\n2. Then today's tasks." };
        var after = new { model = "claude-opus-5", system = "You are helpful.\n1. Tasks alphabetically.\n2. Then today's tasks." };
        var baseline = SnapshotData.FromContext(await Run(("write", before, "b")));

        var detail = SnapshotComparer.Compare(baseline, await Run(("write", after, "b"))).Changes.Single().Detail!;

        var lines = detail.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        lines.Should().Contain(l => l.StartsWith("-") && l.TrimStart('-', ' ') == "1. Overdue tasks first.");
        lines.Should().Contain(l => l.StartsWith("+") && l.TrimStart('+', ' ') == "1. Tasks alphabetically.");
        detail.Should().NotContain("\\n", "multi-line strings are expanded, not shown JSON-escaped");
        detail.Split('\n').Should().HaveCountLessThan(6, "only the changed line and its context are shown");
    }

    [Fact]
    public void SnapshotData_RoundTripsThroughJson_IncludingLegacyShape()
    {
        var legacy = JsonSerializer.Deserialize<SnapshotData>("""{ "step_names": ["a"], "step_order": ["a", "a"], "step_count": 2 }""")!;
        legacy.HasStepContent.Should().BeFalse();
        legacy.StepOrder.Should().Equal("a", "a");

        var v2 = JsonSerializer.Deserialize<SnapshotData>("""{ "version": 2, "name": "n", "steps": [{ "name": "a", "type": "llm", "input": "p", "output": "o" }] }""")!;
        v2.HasStepContent.Should().BeTrue();
        v2.Steps[0].Type.Should().Be("llm");
    }
}
