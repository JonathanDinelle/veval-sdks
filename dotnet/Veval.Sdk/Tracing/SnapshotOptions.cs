namespace Veval.Sdk;

/// <summary>What a snapshot comparison checks beyond the step sequence, which is always compared.</summary>
public class SnapshotOptions
{
    /// <summary>
    /// Compare what the agent sent to each step (prompts, tool arguments). On by default: in replay mode
    /// outputs are recorded, so inputs are where code and prompt changes show up.
    /// </summary>
    public bool CompareInputs { get; set; } = true;

    /// <summary>
    /// Compare each step's output. Off by default because live LLM and API outputs vary run to run;
    /// turn it on for deterministic steps, or use <see cref="Normalize"/> to strip the varying parts.
    /// </summary>
    public bool CompareOutputs { get; set; }

    /// <summary>Steps left out of the comparison entirely (e.g. a timing or logging step).</summary>
    public ISet<string> IgnoreSteps { get; set; } = new HashSet<string>();

    /// <summary>JSON property names dropped from inputs/outputs at any depth before comparing (e.g. "timestamp", "request_id").</summary>
    public ISet<string> IgnoreFields { get; set; } = new HashSet<string>();

    /// <summary>Optional rewrite applied to each input/output before comparing: (stepName, value) => normalized value.</summary>
    public Func<string, object?, object?>? Normalize { get; set; }
}
