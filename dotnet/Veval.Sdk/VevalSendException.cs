namespace Veval.Sdk;

/// <summary>
/// A trace or scenario run could not be delivered to Veval. Thrown only when
/// <see cref="VevalOptions.ThrowOnSendFailure"/> is set; otherwise the failure is logged as a warning.
/// </summary>
public class VevalSendException : Exception
{
    /// <summary>The HTTP status the API answered with, or null when the request never got a response.</summary>
    public int? StatusCode { get; }

    public VevalSendException(string message, int? statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
