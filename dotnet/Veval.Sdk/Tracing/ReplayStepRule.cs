namespace Veval.Sdk;

/// <summary>What a live replay does with a call whose input matches no unused recording of its step.</summary>
public enum OnDivergence
{
    /// <summary>Stop the run at that step. The replay ends inconclusive: neither passed nor failed.</summary>
    Stop,
    /// <summary>Serve the next unused recording of that step and report the call as diverged in the fidelity report.</summary>
    Serve,
}

/// <summary>How replay matches calls of one step to its recordings, and what a live replay does when none matches.</summary>
public class ReplayStepRule
{
    /// <summary>
    /// Match calls on the step name alone, so a changed input never counts as divergence. For steps whose input
    /// doesn't affect what they return, such as narration whose wording follows the model.
    /// </summary>
    public bool MatchOnNameOnly { get; set; }

    /// <summary>Input property names dropped at any depth before matching (e.g. "reasoning", "request_id").</summary>
    public ISet<string> IgnoreInputFields { get; set; } = new HashSet<string>();

    /// <summary>
    /// Live replay: what a diverged call does. Null uses <see cref="ReplayOptions.StopOnDivergenceTypes"/>: stop
    /// when the step was recorded with one of those types, serve otherwise.
    /// </summary>
    public OnDivergence? OnDivergence { get; set; }
}

/// <summary>Why a replay stopped.</summary>
public enum ReplayStopReason
{
    /// <summary>The call's input matched no unused recording of its step, and the step stops on divergence.</summary>
    Diverged,
    /// <summary>The recording has no unused call of that step to serve, and it isn't a model step that can run live.</summary>
    NoRecording,
    /// <summary>
    /// An injected fault (<see cref="ReplayOptions.Faults"/>) sent the agent off its recording: this call matches no
    /// unused recording. Past the fault the recording can't say what the real step would return.
    /// </summary>
    AfterFault,
}

/// <summary>
/// Where a live replay left its recording and stopped. Past this step nobody knows what the real tools would have
/// returned, so the replay is inconclusive: its assertions are not evaluated and it neither passes nor fails.
/// </summary>
/// <param name="Step">The 1-based position of the call among the replay's steps.</param>
/// <param name="RecordedInput">The input of the recording the call was compared with; null when there was none.</param>
public record ReplayStop(int Step, string StepName, ReplayStopReason Reason, object? RecordedInput, object? ActualInput, string Message);

/// <summary>
/// Thrown from a step when a live replay stops there (<see cref="ReplayStop"/>). Let it propagate: if the agent catches
/// it, the replay is still inconclusive, and every later step throws it again.
/// </summary>
public class ReplayStoppedException : Exception
{
    public ReplayStop Stop { get; }

    public ReplayStoppedException(ReplayStop stop) : base(stop.Message) => Stop = stop;
}
