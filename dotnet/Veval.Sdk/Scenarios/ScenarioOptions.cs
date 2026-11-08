namespace Veval.Sdk;

/// <summary>Settings for every item of a scenario run (<c>RunScenarioAsync</c>).</summary>
public class ScenarioOptions
{
    /// <summary>
    /// Replay every trace-backed item fully mocked, as if each set <see cref="ScenarioItem.MockModelSteps"/>; this is
    /// the switch for items loaded from the dashboard. <see cref="VevalTestSdk"/> always replays mocked.
    /// </summary>
    public bool MockModelSteps { get; set; }

    /// <summary>
    /// <see cref="ReplayOptions.StepRules"/> for every trace-backed item; an item's own <see cref="ScenarioItem.StepRules"/>
    /// entry for the same step replaces the scenario's.
    /// </summary>
    public Dictionary<string, ReplayStepRule> StepRules { get; set; } = new();

    /// <summary>
    /// The largest share of replayed items (0 to 1) that may be inconclusive in a passing run. Defaults to 0: any inconclusive item
    /// fails the run, so a change that sends every item off its recorded path can't look green. Raise it to accept
    /// some items needing a real run, e.g. 0.1 for one in ten.
    /// </summary>
    public double MaxInconclusiveRate
    {
        get => _maxInconclusiveRate;
        set => _maxInconclusiveRate = value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxInconclusiveRate), value, "MaxInconclusiveRate is a share from 0 to 1.");
    }

    private double _maxInconclusiveRate;
}
