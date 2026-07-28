namespace Veval.Sdk.Assertions;

public interface ITraceAssertion
{
    string? Evaluate(VevalExecutionContext ctx);
}
