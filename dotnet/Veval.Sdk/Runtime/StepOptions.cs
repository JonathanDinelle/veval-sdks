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
    /// The step's type (<see cref="StepType"/>), known before the step runs. A live replay uses it for a step the
    /// recording never contained: a model step (<c>llm</c>) then runs live instead of stopping the replay. When the
    /// recording has the step, its recorded type wins, and a different type here is reported on the binding
    /// (<see cref="ReplayBinding.DeclaredType"/>). New recordings carry this type; <c>SetMeta("type", …)</c> inside
    /// the step still overrides it.
    /// </summary>
    public string? Type { get; set; }
}
