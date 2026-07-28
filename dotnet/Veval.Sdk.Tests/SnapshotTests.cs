using FluentAssertions;
using Veval.Sdk;

namespace Veval.Sdk.Tests;

public class SnapshotTests
{
    [Fact]
    public async Task CompareSnapshot_IdenticalTraces_NoDiff()
    {
        var snapshot = new SnapshotData { StepNames = new List<string> { "classify", "respond" }, StepOrder = new List<string> { "classify", "respond" }, StepCount = 2 };
        var ctx = new VevalExecutionContext("test", null);
        await ctx.TrackStepAsync("classify", null, () => Task.FromResult("a"));
        await ctx.TrackStepAsync("respond",  null, () => Task.FromResult("b"));
        var diff = SnapshotComparer.Compare(snapshot, ctx);
        diff.HasChanges.Should().BeFalse();
    }

    [Fact]
    public async Task CompareSnapshot_AddedStep_DetectsDiff()
    {
        var snapshot = new SnapshotData { StepNames = new List<string> { "classify" }, StepOrder = new List<string> { "classify" }, StepCount = 1 };
        var ctx = new VevalExecutionContext("test", null);
        await ctx.TrackStepAsync("classify", null, () => Task.FromResult("a"));
        await ctx.TrackStepAsync("new-step", null, () => Task.FromResult("b"));
        var diff = SnapshotComparer.Compare(snapshot, ctx);
        diff.HasChanges.Should().BeTrue();
        diff.AddedSteps.Should().Contain("new-step");
    }

    [Fact]
    public async Task CompareSnapshot_RemovedStep_DetectsDiff()
    {
        var snapshot = new SnapshotData { StepNames = new List<string> { "classify", "respond" }, StepOrder = new List<string> { "classify", "respond" }, StepCount = 2 };
        var ctx = new VevalExecutionContext("test", null);
        await ctx.TrackStepAsync("classify", null, () => Task.FromResult("a"));
        var diff = SnapshotComparer.Compare(snapshot, ctx);
        diff.HasChanges.Should().BeTrue();
        diff.RemovedSteps.Should().Contain("respond");
    }

    [Fact]
    public async Task CompareSnapshot_OrderChanged_DetectsDiff()
    {
        var snapshot = new SnapshotData { StepNames = new List<string> { "classify", "respond" }, StepOrder = new List<string> { "classify", "respond" }, StepCount = 2 };
        var ctx = new VevalExecutionContext("test", null);
        await ctx.TrackStepAsync("respond",  null, () => Task.FromResult("b"));
        await ctx.TrackStepAsync("classify", null, () => Task.FromResult("a"));
        var diff = SnapshotComparer.Compare(snapshot, ctx);
        diff.HasChanges.Should().BeTrue();
        diff.OrderChanges.Should().NotBeEmpty();
    }

    [Fact]
    public async Task CreateSnapshot_FromContext_CapturesStepInfo()
    {
        var ctx = new VevalExecutionContext("test", null);
        await ctx.TrackStepAsync("classify", null, () => Task.FromResult("a"));
        await ctx.TrackStepAsync("respond",  null, () => Task.FromResult("b"));
        var snapshot = SnapshotData.FromContext(ctx);
        snapshot.StepNames.Should().BeEquivalentTo(new[] { "classify", "respond" });
        snapshot.StepCount.Should().Be(2);
    }
}
