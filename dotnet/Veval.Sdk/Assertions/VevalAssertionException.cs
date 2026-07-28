namespace Veval.Sdk;

public class VevalAssertionException : Exception
{
    public IReadOnlyList<string> Failures { get; }

    public VevalAssertionException(IReadOnlyList<string> failures)
        : base("Veval assertions failed:\n" + string.Join("\n", failures.Select(f => $"  - {f}")))
    {
        Failures = failures;
    }
}
