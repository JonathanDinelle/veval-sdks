using System.Net;

namespace Veval.Sdk;

/// <summary>What a <see cref="ReplayFault"/> serves in place of a step's recorded output.</summary>
public enum FaultKind
{
    /// <summary>Throws a <see cref="TaskCanceledException"/> wrapping a <see cref="TimeoutException"/>, as <c>HttpClient</c> does on a timeout.</summary>
    Timeout,
    /// <summary>Throws a <see cref="FaultHttpException"/> with the fault's status code.</summary>
    HttpError,
    /// <summary>Throws a <see cref="FaultHttpException"/> with status 429 and a <c>Retry-After</c>.</summary>
    RateLimit,
    /// <summary>Returns a malformed value instead of throwing (<see cref="MalformedOutput"/>).</summary>
    Malformed,
    /// <summary>Throws the exception the fault's factory builds.</summary>
    Custom,
}

/// <summary>A malformed output a <see cref="ReplayFault.Malformed(string, MalformedOutput)"/> fault returns.</summary>
public enum MalformedOutput
{
    /// <summary>
    /// The step's recorded output as JSON, cut in half: a string for a step that returns a string or an object.
    /// Needs an unused recording of the step to cut.
    /// </summary>
    Truncated,
    /// <summary>An empty value: <c>""</c> for a string step, <c>null</c> (the type's default) otherwise.</summary>
    Empty,
}

/// <summary>
/// Serves a declared failure instead of a step's recorded output, so a replay exercises the retries, fallbacks and
/// guards a recording almost never contains. Add faults to <see cref="ReplayOptions.Faults"/>. Deterministic: a fault
/// fires on the calls it targets and nowhere else.
/// <para>
/// By default a fault fires on every call of its step. <see cref="Times"/> limits it to the first n matching calls,
/// after which the step is served from the recording again, which tests recovery. A faulted call never uses up a
/// recording, so a retry with the same input gets the recorded output. A fault that never fires fails the replay.
/// </para>
/// </summary>
public sealed class ReplayFault
{
    private Func<Exception>? _exceptionFactory;
    private object? _input;

    /// <summary>The step the fault targets.</summary>
    public string StepName { get; }
    public FaultKind Kind { get; }
    /// <summary>The HTTP status of an <see cref="FaultKind.HttpError"/> or <see cref="FaultKind.RateLimit"/> fault.</summary>
    public int? StatusCode { get; }
    /// <summary>The <c>Retry-After</c> of a <see cref="FaultKind.RateLimit"/> fault.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>What a <see cref="FaultKind.Malformed"/> fault returns, unless it returns a <see cref="MalformedValue"/>.</summary>
    public MalformedOutput? MalformedOutput { get; }
    /// <summary>The value a <see cref="FaultKind.Malformed"/> fault built with <see cref="MalformedValue"/> returns.</summary>
    public object? MalformedValue { get; }
    /// <summary>How many matching calls the fault fails; null fails every one.</summary>
    public int? Count { get; private set; }
    /// <summary>The 1-based call of the step from which the fault applies; earlier calls are served as usual.</summary>
    public int FromCall { get; private set; } = 1;
    /// <summary>When true, the fault only applies to calls whose input equals <see cref="Input"/>.</summary>
    public bool HasInput { get; private set; }
    /// <summary>The input a call must have for the fault to apply, when <see cref="HasInput"/> is true.</summary>
    public object? Input => _input;

    private ReplayFault(string stepName, FaultKind kind, int? statusCode = null, TimeSpan? retryAfter = null,
        MalformedOutput? malformed = null, object? malformedValue = null, Func<Exception>? factory = null)
    {
        if (string.IsNullOrEmpty(stepName)) throw new ArgumentException("A fault needs a step name.", nameof(stepName));
        StepName = stepName;
        Kind = kind;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        MalformedOutput = malformed;
        MalformedValue = malformedValue;
        _exceptionFactory = factory;
    }

    /// <summary>The step times out: <see cref="TaskCanceledException"/> wrapping a <see cref="TimeoutException"/>.</summary>
    public static ReplayFault Timeout(string stepName) => new(stepName, FaultKind.Timeout);

    /// <summary>The step fails with an HTTP error status, e.g. 500 or 503 (<see cref="FaultHttpException"/>).</summary>
    public static ReplayFault HttpError(string stepName, int statusCode)
    {
        if (statusCode < 400 || statusCode > 599)
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "An HTTP error status is between 400 and 599.");
        return new(stepName, FaultKind.HttpError, statusCode);
    }

    /// <summary>The step is rate limited: status 429 with a <c>Retry-After</c> (one second unless given).</summary>
    public static ReplayFault RateLimit(string stepName, TimeSpan? retryAfter = null)
    {
        var wait = retryAfter ?? TimeSpan.FromSeconds(1);
        if (wait < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryAfter), "Retry-After can't be negative.");
        return new(stepName, FaultKind.RateLimit, 429, wait);
    }

    /// <summary>The step returns a malformed output instead of throwing, so the guard that parses it runs.</summary>
    public static ReplayFault Malformed(string stepName, MalformedOutput output) =>
        new(stepName, FaultKind.Malformed, malformed: output);

    /// <summary>
    /// The step returns this value instead of its recorded output, e.g. a value of the wrong type. It must convert to
    /// the step's return type, as a recorded output does.
    /// </summary>
    public static ReplayFault Malformed(string stepName, object? value) =>
        new(stepName, FaultKind.Malformed, malformedValue: value);

    /// <summary>The step throws the exception this factory builds, for clients with their own exception types.</summary>
    public static ReplayFault Throw(string stepName, Func<Exception> exceptionFactory) =>
        new(stepName, FaultKind.Custom, factory: exceptionFactory ?? throw new ArgumentNullException(nameof(exceptionFactory)));

    /// <summary>Fail only the first <paramref name="count"/> matching calls, then serve the recording again.</summary>
    public ReplayFault Times(int count)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "A fault fires at least once.");
        Count = count;
        return this;
    }

    /// <summary>Apply from the <paramref name="call"/>-th call of the step (1-based, retries included).</summary>
    public ReplayFault OnCall(int call)
    {
        if (call < 1) throw new ArgumentOutOfRangeException(nameof(call), call, "Calls are numbered from 1.");
        FromCall = call;
        return this;
    }

    /// <summary>
    /// Apply only to calls whose input equals this one, compared as replay matches recordings (key order and the
    /// step's <see cref="ReplayStepRule.IgnoreInputFields"/> aside).
    /// </summary>
    public ReplayFault WhenInput(object? input)
    {
        _input = input;
        HasInput = true;
        return this;
    }

    /// <summary>A short label, e.g. <c>Timeout on 'search_flights'</c> or <c>HTTP 503 on 'book_hotel' (first 2 calls)</c>.</summary>
    public string Description
    {
        get
        {
            var kind = Kind switch
            {
                FaultKind.Timeout => "Timeout",
                FaultKind.HttpError => $"HTTP {StatusCode}",
                FaultKind.RateLimit => $"Rate limit (429, Retry-After {RetryAfter!.Value.TotalSeconds:0.###}s)",
                FaultKind.Malformed => MalformedOutput is { } m ? $"Malformed output ({m.ToString().ToLowerInvariant()})" : "Malformed output (value)",
                _ => "Custom exception",
            };
            var scope = new List<string>();
            if (Count is { } n) scope.Add(n == 1 ? "first call" : $"first {n} calls");
            if (FromCall > 1) scope.Add($"from call {FromCall}");
            if (HasInput) scope.Add("matching input");
            return $"{kind} on '{StepName}'" + (scope.Count > 0 ? $" ({string.Join(", ", scope)})" : "");
        }
    }

    public override string ToString() => Description;

    /// <summary>The exception a throwing fault raises at a call of its step.</summary>
    internal Exception CreateException()
    {
        var message = $"Injected fault: {Description}.";
        return Kind switch
        {
            FaultKind.Timeout => new TaskCanceledException(message, new TimeoutException(message)),
            FaultKind.HttpError or FaultKind.RateLimit => new FaultHttpException(message, StatusCode!.Value, RetryAfter),
            FaultKind.Custom => _exceptionFactory!() ?? throw new InvalidOperationException(
                $"The exception factory of the fault on '{StepName}' returned null."),
            _ => throw new InvalidOperationException($"A {Kind} fault doesn't throw."),
        };
    }
}

/// <summary>
/// The HTTP failure an <see cref="ReplayFault.HttpError"/> or <see cref="ReplayFault.RateLimit"/> fault throws. It is an
/// <see cref="HttpRequestException"/> with <see cref="HttpRequestException.StatusCode"/> set, so code that handles
/// those catches it; <see cref="RetryAfter"/> and <see cref="Headers"/> carry what a 429 response would.
/// </summary>
public class FaultHttpException : HttpRequestException
{
    public int Status { get; }
    /// <summary>The 429's <c>Retry-After</c>, or null.</summary>
    public TimeSpan? RetryAfter { get; }
    /// <summary>The response headers the fault simulates: <c>Retry-After</c> (in seconds) for a rate limit.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    public FaultHttpException(string message, int status, TimeSpan? retryAfter = null)
        : base(message, null, (HttpStatusCode)status)
    {
        Status = status;
        RetryAfter = retryAfter;
        Headers = retryAfter is { } wait
            ? new Dictionary<string, string> { ["Retry-After"] = Math.Ceiling(wait.TotalSeconds).ToString("0") }
            : new Dictionary<string, string>();
    }
}

/// <summary>How many times one fault fired during a replay.</summary>
public record FaultReport(ReplayFault Fault, int Fired)
{
    public bool Triggered => Fired > 0;
}
