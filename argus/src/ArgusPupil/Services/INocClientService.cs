using ArgusPupil.Models;

namespace ArgusPupil.Services;

/// <summary>
/// Result of a NOC send operation
/// </summary>
public class NocSendResult
{
    /// <summary>
    /// Whether the send was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Number of retry attempts made
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// HTTP status code from NOC service (if available)
    /// </summary>
    public int? StatusCode { get; set; }

    public static NocSendResult Succeeded(int retryCount = 0, int? statusCode = null) => new()
    {
        Success = true,
        RetryCount = retryCount,
        StatusCode = statusCode
    };

    public static NocSendResult Failed(string errorMessage, int retryCount = 0, int? statusCode = null) => new()
    {
        Success = false,
        ErrorMessage = errorMessage,
        RetryCount = retryCount,
        StatusCode = statusCode
    };
}

/// <summary>
/// Service for sending messages to NOC
/// </summary>
public interface INocClientService
{
    /// <summary>
    /// Send NOC details to the NOC service
    /// </summary>
    /// <param name="nocDetails">The NOC details to send</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the send operation</returns>
    Task<NocSendResult> SendAsync(NocDetails nocDetails, string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Send NOC details with source context (e.g., "watchdog", "recovery")
    /// </summary>
    /// <param name="nocDetails">The NOC details to send</param>
    /// <param name="source">Source context for the message</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the send operation</returns>
    Task<NocSendResult> SendAsync(NocDetails nocDetails, string source, string correlationId, CancellationToken cancellationToken = default);
}

