using System.Text;
using Veval.Sdk.Internal;

namespace Veval.Sdk;

public enum SnapshotChangeKind
{
    /// <summary>A step ran that the snapshot doesn't have (including an extra call of a known step).</summary>
    Added,
    /// <summary>A snapshot step didn't run.</summary>
    Removed,
    /// <summary>A step ran, but at a different point in the sequence.</summary>
    Moved,
    /// <summary>The step received different input — e.g. a changed prompt or tool argument.</summary>
    InputChanged,
    /// <summary>The step returned different output (only when <see cref="SnapshotOptions.CompareOutputs"/> is on).</summary>
    OutputChanged,
}

public class SnapshotChange
{
    public SnapshotChangeKind Kind { get; set; }
    public string StepName { get; set; } = "";
    /// <summary>Which call of this step name, 1-based — distinguishes repeated calls of the same step.</summary>
    public int Occurrence { get; set; } = 1;
    public int? ExpectedIndex { get; set; }
    public int? ActualIndex { get; set; }
    /// <summary>Canonical expected value, for input/output changes.</summary>
    public string? Expected { get; set; }
    /// <summary>Canonical actual value, for input/output changes.</summary>
    public string? Actual { get; set; }
    /// <summary>A short line diff of <see cref="Expected"/> vs <see cref="Actual"/>.</summary>
    public string? Detail { get; set; }

    public string Label => Occurrence > 1 ? $"'{StepName}' (call #{Occurrence})" : $"'{StepName}'";

    public override string ToString() => Kind switch
    {
        SnapshotChangeKind.Added => $"+ added {Label} at position {ActualIndex}",
        SnapshotChangeKind.Removed => $"- removed {Label} (was at position {ExpectedIndex})",
        SnapshotChangeKind.Moved => $"~ moved {Label} from position {ExpectedIndex} to {ActualIndex}",
        SnapshotChangeKind.InputChanged => $"~ input changed for {Label}",
        SnapshotChangeKind.OutputChanged => $"~ output changed for {Label}",
        _ => Kind.ToString(),
    };
}

/// <summary>One row of the expected-vs-actual step alignment, in run order.</summary>
public class SnapshotAlignmentRow
{
    /// <summary>"match", "added", or "removed".</summary>
    public string Kind { get; set; } = "match";
    public string StepName { get; set; } = "";
    public int? ExpectedIndex { get; set; }
    public int? ActualIndex { get; set; }
    public bool InputChanged { get; set; }
    public bool OutputChanged { get; set; }
}

public class SnapshotDiff
{
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Every difference found, in run order.</summary>
    public List<SnapshotChange> Changes { get; set; } = new();

    /// <summary>Expected and actual steps side by side, in run order.</summary>
    public List<SnapshotAlignmentRow> Alignment { get; set; } = new();

    public List<SnapshotStep> ExpectedSteps { get; set; } = new();
    public List<SnapshotStep> ActualSteps { get; set; } = new();

    /// <summary>False for a legacy (names-only) snapshot: only the step sequence could be compared.</summary>
    public bool ComparedContent { get; set; }

    // Name-level views, kept for existing callers.
    public List<string> AddedSteps { get; set; } = new();
    public List<string> RemovedSteps { get; set; } = new();
    public List<string> OrderChanges { get; set; } = new();

    /// <summary>A readable report of every change, with prompt/input diffs — suitable for a test failure message.</summary>
    public string Summary(string? snapshotName = null)
    {
        var title = snapshotName is null ? "Snapshot" : $"Snapshot '{snapshotName}'";
        if (!HasChanges) return $"{title}: no changes.";

        var sb = new StringBuilder($"{title}: {Changes.Count} change(s)");
        foreach (var change in Changes)
        {
            sb.AppendLine().Append("  ").Append(change);
            if (change.Detail is { Length: > 0 } detail)
                foreach (var line in detail.Split('\n'))
                    sb.AppendLine().Append("      ").Append(line.TrimEnd('\r'));
        }
        if (!ComparedContent)
            sb.AppendLine().Append("  (legacy snapshot: step inputs were not recorded, so only the sequence was compared)");
        return sb.ToString();
    }

    public override string ToString() => Summary();
}

public static class SnapshotComparer
{
    public static SnapshotDiff Compare(SnapshotData snapshot, VevalExecutionContext ctx, SnapshotOptions? options = null) =>
        Compare(snapshot, SnapshotData.FromContext(ctx), options);

    public static SnapshotDiff Compare(SnapshotData snapshot, SnapshotData actual, SnapshotOptions? options = null)
    {
        options ??= new SnapshotOptions();
        var expected = snapshot.OrderedSteps.Where(s => !options.IgnoreSteps.Contains(s.Name)).ToList();
        var current = actual.OrderedSteps.Where(s => !options.IgnoreSteps.Contains(s.Name)).ToList();
        var compareContent = snapshot.HasStepContent;

        var diff = new SnapshotDiff { ExpectedSteps = expected, ActualSteps = current, ComparedContent = compareContent };
        var expectedOccurrence = Occurrences(expected);
        var actualOccurrence = Occurrences(current);

        // Align the two sequences on step name (longest common subsequence), so an inserted, dropped,
        // repeated, or reordered step shows up exactly where it happened.
        var rows = Align(expected, current);
        diff.Alignment = rows;

        // A step that was removed in one place and added in another has moved.
        var removed = rows.Where(r => r.Kind == "removed").ToList();
        var added = rows.Where(r => r.Kind == "added").ToList();
        var moved = new List<(SnapshotAlignmentRow From, SnapshotAlignmentRow To)>();
        foreach (var r in removed.ToList())
        {
            var match = added.FirstOrDefault(a => a.StepName == r.StepName);
            if (match is null) continue;
            moved.Add((r, match));
            removed.Remove(r);
            added.Remove(match);
        }

        foreach (var row in rows)
        {
            if (row.Kind == "removed" && removed.Contains(row))
            {
                diff.Changes.Add(new SnapshotChange
                {
                    Kind = SnapshotChangeKind.Removed, StepName = row.StepName,
                    Occurrence = expectedOccurrence[row.ExpectedIndex!.Value], ExpectedIndex = row.ExpectedIndex,
                });
                diff.RemovedSteps.Add(row.StepName);
            }
            else if (row.Kind == "added" && added.Contains(row))
            {
                diff.Changes.Add(new SnapshotChange
                {
                    Kind = SnapshotChangeKind.Added, StepName = row.StepName,
                    Occurrence = actualOccurrence[row.ActualIndex!.Value], ActualIndex = row.ActualIndex,
                });
                diff.AddedSteps.Add(row.StepName);
            }
            else if (row.Kind == "added" && moved.FirstOrDefault(m => m.To == row) is { From: not null } move)
            {
                var change = new SnapshotChange
                {
                    Kind = SnapshotChangeKind.Moved, StepName = row.StepName,
                    Occurrence = actualOccurrence[row.ActualIndex!.Value],
                    ExpectedIndex = move.From.ExpectedIndex, ActualIndex = row.ActualIndex,
                };
                diff.Changes.Add(change);
                diff.OrderChanges.Add($"'{row.StepName}' moved from position {move.From.ExpectedIndex} to {row.ActualIndex}");
            }

            if (compareContent && row.Kind == "match")
                CompareContent(row, expected[row.ExpectedIndex!.Value], current[row.ActualIndex!.Value],
                    actualOccurrence[row.ActualIndex.Value], options, diff);
        }

        return diff;
    }

    private static void CompareContent(
        SnapshotAlignmentRow row, SnapshotStep expected, SnapshotStep actual, int occurrence,
        SnapshotOptions options, SnapshotDiff diff)
    {
        if (options.CompareInputs && Differs(expected.Name, expected.Input, actual.Input, options, out var e, out var a))
        {
            row.InputChanged = true;
            diff.Changes.Add(ContentChange(SnapshotChangeKind.InputChanged, row, occurrence, e, a));
        }
        if (options.CompareOutputs && Differs(expected.Name, expected.Output, actual.Output, options, out e, out a))
        {
            row.OutputChanged = true;
            diff.Changes.Add(ContentChange(SnapshotChangeKind.OutputChanged, row, occurrence, e, a));
        }
    }

    private static bool Differs(string stepName, object? expected, object? actual, SnapshotOptions options,
        out string expectedText, out string actualText)
    {
        if (options.Normalize is { } normalize)
        {
            expected = normalize(stepName, expected);
            actual = normalize(stepName, actual);
        }
        var ignore = options.IgnoreFields.ToList();
        if (SnapshotValues.Canonicalize(expected, ignore) == SnapshotValues.Canonicalize(actual, ignore))
        {
            expectedText = actualText = "";
            return false;
        }

        // Equality is decided on canonical JSON; the reported values use the readable form for diffing.
        expectedText = SnapshotValues.Display(expected, ignore);
        actualText = SnapshotValues.Display(actual, ignore);
        return true;
    }

    private static SnapshotChange ContentChange(SnapshotChangeKind kind, SnapshotAlignmentRow row, int occurrence, string expected, string actual) => new()
    {
        Kind = kind, StepName = row.StepName, Occurrence = occurrence,
        ExpectedIndex = row.ExpectedIndex, ActualIndex = row.ActualIndex,
        Expected = expected, Actual = actual, Detail = SnapshotValues.LineDiff(expected, actual),
    };

    /// <summary>For each position, which call of that step name it is (1-based).</summary>
    private static int[] Occurrences(IReadOnlyList<SnapshotStep> steps)
    {
        var counts = new Dictionary<string, int>();
        var result = new int[steps.Count];
        for (var i = 0; i < steps.Count; i++)
        {
            counts[steps[i].Name] = counts.TryGetValue(steps[i].Name, out var c) ? c + 1 : 1;
            result[i] = counts[steps[i].Name];
        }
        return result;
    }

    private static List<SnapshotAlignmentRow> Align(IReadOnlyList<SnapshotStep> expected, IReadOnlyList<SnapshotStep> actual)
    {
        var n = expected.Count;
        var m = actual.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = expected[i].Name == actual[j].Name
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var rows = new List<SnapshotAlignmentRow>();
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && expected[x].Name == actual[y].Name)
            {
                rows.Add(new SnapshotAlignmentRow { Kind = "match", StepName = expected[x].Name, ExpectedIndex = x, ActualIndex = y });
                x++; y++;
            }
            else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                rows.Add(new SnapshotAlignmentRow { Kind = "added", StepName = actual[y].Name, ActualIndex = y });
                y++;
            }
            else
            {
                rows.Add(new SnapshotAlignmentRow { Kind = "removed", StepName = expected[x].Name, ExpectedIndex = x });
                x++;
            }
        }
        return rows;
    }
}
