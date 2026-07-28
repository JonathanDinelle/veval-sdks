namespace Veval.Sdk;

public class SnapshotDiff
{
    public bool HasChanges => AddedSteps.Count > 0 || RemovedSteps.Count > 0 || OrderChanges.Count > 0;
    public List<string> AddedSteps { get; set; } = new();
    public List<string> RemovedSteps { get; set; } = new();
    public List<string> OrderChanges { get; set; } = new();
}

public static class SnapshotComparer
{
    public static SnapshotDiff Compare(SnapshotData snapshot, VevalExecutionContext ctx)
    {
        var current = SnapshotData.FromContext(ctx);
        var diff = new SnapshotDiff();

        var snapshotSet = new HashSet<string>(snapshot.StepNames);
        var currentSet = new HashSet<string>(current.StepNames);

        diff.AddedSteps = currentSet.Except(snapshotSet).ToList();
        diff.RemovedSteps = snapshotSet.Except(currentSet).ToList();

        var minLen = Math.Min(snapshot.StepOrder.Count, current.StepOrder.Count);
        for (int i = 0; i < minLen; i++)
        {
            if (snapshot.StepOrder[i] != current.StepOrder[i])
                diff.OrderChanges.Add($"Position {i}: expected '{snapshot.StepOrder[i]}', got '{current.StepOrder[i]}'");
        }

        return diff;
    }
}
