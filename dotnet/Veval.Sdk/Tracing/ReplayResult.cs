namespace Veval.Sdk;

public class ReplayResult
{
    public bool Passed => Failures.Count == 0;
    public List<string> Failures { get; set; } = new();
    public VevalExecutionContext? ReplayedContext { get; set; }
    public object? Output { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CompletedAt { get; set; }
    public string Status { get; set; } = "success";
    public string? Error { get; set; }
}
