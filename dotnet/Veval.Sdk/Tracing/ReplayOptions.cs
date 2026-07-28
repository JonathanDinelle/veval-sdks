using Veval.Sdk.Assertions;

namespace Veval.Sdk;

public class ReplayOptions
{
    public bool MockLlmResponses { get; set; }
    public ITraceAssertion[] Assertions { get; set; } = Array.Empty<ITraceAssertion>();
}
