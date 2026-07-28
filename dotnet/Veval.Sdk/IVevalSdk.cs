using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public interface IVevalSdk : IDisposable
{
    Task<T> RunAsync<T>(string agentName, Func<VevalExecutionContext, Task<T>> callback, object? input = null);
    Task<TraceData?> GetTraceAsync(string traceId);
    Task<SnapshotData?> LoadSnapshotAsync(string traceId);
    Task<SnapshotDiff> CompareSnapshotAsync(string snapshotName, SnapshotData snapshot, VevalExecutionContext ctx);
    Task<ReplayResult> ReplayAsync<T>(TraceData trace, Func<VevalExecutionContext, Task<T>> callback, ReplayOptions options);
    Task<ScenarioRunResult> RunScenarioAsync<T>(string scenarioName, Func<VevalExecutionContext, Task<T>> agent, ITraceAssertion[] scenarioAssertions, ScenarioItem[]? items = null);
    Task<JudgeResult> JudgeAsync(string criteria, VevalExecutionContext ctx, JudgeOptions? options = null);
}
