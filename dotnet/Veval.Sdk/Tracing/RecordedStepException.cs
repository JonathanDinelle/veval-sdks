namespace Veval.Sdk;

/// <summary>
/// Thrown from a step when replay serves a recording of that step that failed in the recorded run, so the agent's retry
/// or fallback runs as it did then. It carries the recorded error message, not the original exception's type: code
/// that only catches a specific exception type won't catch it.
/// </summary>
public class RecordedStepException : Exception
{
    public string StepName { get; }
    /// <summary>The error the recorded step failed with, or null when the recording didn't keep one.</summary>
    public string? RecordedError { get; }

    public RecordedStepException(string stepName, string? recordedError)
        : base($"Step '{stepName}' failed in the recorded run: {recordedError ?? "no error message recorded"}")
    {
        StepName = stepName;
        RecordedError = recordedError;
    }
}
