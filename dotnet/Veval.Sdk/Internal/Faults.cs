namespace Veval.Sdk.Internal;

internal static class Faults
{
    /// <summary>Whether replay served this step an injected fault (<see cref="ReplayOptions.Faults"/>).</summary>
    public static bool IsInjected(Step step) =>
        step.Metadata.TryGetValue("_source", out var source) && source as string == "fault";
}
