namespace Veval.Sdk;

/// <summary>Step types Veval knows. <see cref="Step.Type"/> is a string, so these are its usual values.</summary>
public static class StepType
{
    /// <summary>A model call. Live replay runs it for real.</summary>
    public const string Llm = "llm";
    /// <summary>A tool call. Replay serves it from the recording, and a live replay stops when its input diverges.</summary>
    public const string Tool = "tool";
    /// <summary>Anything else; the default.</summary>
    public const string Custom = "custom";
}

/// <summary>Settings for one step, given where the step is called.</summary>
public class StepOptions
{
    /// <summary>
    /// The step's type (<see cref="StepType"/>), known before the step runs. A live replay uses it when the recording
    /// doesn't know the step's type: a step it never contained, or one saved untyped (<c>custom</c>). A model step
    /// (<c>llm</c>) then runs live instead of stopping the replay or being served its old output. A recorded
    /// <c>llm</c> or <c>tool</c> type wins, and a different type here is reported on the binding
    /// (<see cref="ReplayBinding.DeclaredType"/>), so a mislabelled call can't turn a recorded tool step live. New
    /// recordings carry this type; <c>SetMeta("type", …)</c> inside the step still overrides it.
    /// </summary>
    public string? Type { get; set; }
}
