namespace Veval.Sdk;

/// <summary>
/// Loading something the SDK needs from Veval failed, such as a scenario's items or a trace to replay.
/// Thrown whatever <see cref="VevalOptions.ThrowOnSendFailure"/> says, since the caller can't continue without it.
/// </summary>
public class VevalRequestException : InvalidOperationException
{
    /// <summary>The HTTP status the API answered with, or null when the request never got a response.</summary>
    public int? StatusCode { get; }

    public VevalRequestException(string message, int? statusCode, Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }
}
