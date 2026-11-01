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
}
