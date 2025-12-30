using ArgusPupil.Models;

namespace ArgusPupil.Services;

/// <summary>
/// Result of message processing
/// </summary>
public class MessageProcessResult
{
    /// <summary>
    /// Whether the message was processed successfully
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if processing failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether the application should shutdown after processing
    /// </summary>
    public bool ShouldShutdown { get; set; }

    public static MessageProcessResult Succeeded() => new() { Success = true };
    public static MessageProcessResult Failed(string error) => new() { Success = false, ErrorMessage = error };
    public static MessageProcessResult ShutdownRequired() => new() { Success = true, ShouldShutdown = true };
}

/// <summary>
/// Service for handling incoming messages and routing to appropriate handlers
/// </summary>
public interface IMessageHandlerService
{
    /// <summary>
    /// Process an incoming pupil request
    /// </summary>
    /// <param name="request">The incoming request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Processing result</returns>
    Task<MessageProcessResult> ProcessAsync(PupilRequest request, CancellationToken cancellationToken = default);
}

