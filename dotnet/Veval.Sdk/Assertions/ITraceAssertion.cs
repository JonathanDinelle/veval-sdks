namespace Veval.Sdk.Assertions;

public interface ITraceAssertion
{
    Task<string?> EvaluateAsync(VevalExecutionContext ctx);
}
