namespace Veval.Sdk.Assertions;

public class JudgeOptions
{
    /// <summary>Must be in the provider's current model catalog, or the judge call fails.</summary>
    public string? Model { get; set; }

    /// <summary>Pass/fail cutoff on the 0.0-1.0 score. Defaults server-side to 0.7 if omitted.</summary>
    public double? Threshold { get; set; }

    /// <summary>An example of an output that fully satisfies the rubric, included in the grading prompt.</summary>
    public object? ReferenceOutput { get; set; }

    /// <summary>
    /// Number of independent grading calls to average via median, for self-consistency.
    /// Clamped server-side to [1, 5]. Each sample is billed, so higher values cost proportionally more.
    /// </summary>
    public int? Samples { get; set; }
}
