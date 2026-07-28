using FluentAssertions;
using Veval.Sdk;

namespace Veval.Sdk.Tests;

public class StepTests
{
    [Fact]
    public void Step_TracksStartTime()
    {
        var step = new Step("test-step", null);
        step.StartedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Complete_SetsOutputAndDuration()
    {
        var step = new Step("test-step", null);
        Thread.Sleep(10);
        step.Complete("result");

        step.Output.Should().Be("result");
        step.Status.Should().Be("success");
        step.DurationMs.Should().BeGreaterThan(0);
        step.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public void Fail_SetsErrorAndStatus()
    {
        var step = new Step("test-step", null);
        step.Fail("something broke");

        step.Error.Should().Be("something broke");
        step.Status.Should().Be("error");
    }

    [Fact]
    public void NestedSteps_TracksParent()
    {
        var parent = new Step("parent", null);
        var child = parent.CreateStep("child");

        child.ParentStepId.Should().Be(parent.StepId);
    }
}
