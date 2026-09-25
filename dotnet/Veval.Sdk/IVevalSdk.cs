using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public interface IVevalSdk : IDisposable
{
    Task<T> RunAsync<T>(string agentName, Func<VevalExecutionContext, Task<T>> callback, object? input = null);
    Task<TraceData?> GetTraceAsync(string traceId);

    /// <summary>Builds a snapshot from a trace on the fly. Prefer <see cref="SaveSnapshotAsync(string, string)"/>, which survives trace retention.</summary>
    Task<SnapshotData?> LoadSnapshotAsync(string traceId);

    /// <summary>
    /// Stores a named baseline built from a recorded trace — every step with its input and output — and pins
    /// the trace so retention never deletes it. Saving again under the same name replaces the baseline.
    /// </summary>
    Task<SnapshotData> SaveSnapshotAsync(string snapshotName, string traceId);

    /// <summary>Stores a named baseline from a run you just executed (e.g. a replay or a test run).</summary>
    Task<SnapshotData> SaveSnapshotAsync(string snapshotName, VevalExecutionContext ctx);

    /// <summary>The latest stored baseline with this name, or null if none exists.</summary>
    Task<SnapshotData?> GetSnapshotAsync(string snapshotName);

    /// <summary>Compares a run against a baseline and records the result in the dashboard.</summary>
    Task<SnapshotDiff> CompareSnapshotAsync(string snapshotName, SnapshotData snapshot, VevalExecutionContext ctx, SnapshotOptions? options = null);

    Task<ReplayResult> ReplayAsync<T>(TraceData trace, Func<VevalExecutionContext, Task<T>> callback, ReplayOptions options);
    Task<ScenarioRunResult> RunScenarioAsync<T>(string scenarioName, Func<VevalExecutionContext, Task<T>> agent, ITraceAssertion[] scenarioAssertions, ScenarioItem[]? items = null);
    Task<JudgeResult> JudgeAsync(string criteria, VevalExecutionContext ctx, JudgeOptions? options = null);
}
