using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArgusPupil.Models;

/// <summary>
/// Incoming HTTP request wrapper that can contain any message type
/// </summary>
public class PupilRequest
{
    /// <summary>
    /// Type of message being sent
    /// </summary>
    public PupilMessageType MessageType { get; set; }

    /// <summary>
    /// Correlation ID for tracing
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the message was created
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// NOC details (used by all message types)
    /// </summary>
    public NocDetails NocDetails { get; set; } = new();

    /// <summary>
    /// Optional timeout override for Heartbeat messages (in seconds)
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Convert to typed message based on MessageType
    /// </summary>
    public PupilMessageBase ToTypedMessage()
    {
        return MessageType switch
        {
            PupilMessageType.Heartbeat => new HeartbeatMessage
            {
                CorrelationId = CorrelationId,
                Timestamp = Timestamp,
                NocDetails = NocDetails,
                TimeoutSeconds = TimeoutSeconds
            },
            PupilMessageType.SendNocMessage => new SendNocMessageCommand
            {
                CorrelationId = CorrelationId,
                Timestamp = Timestamp,
                NocDetails = NocDetails
            },
            _ => throw new ArgumentException($"Unknown message type: {MessageType}")
        };
    }
}

/// <summary>
/// Response returned by the pupil endpoint
/// </summary>
public class PupilResponse
{
    /// <summary>
    /// Whether the message was accepted
    /// </summary>
    public bool Accepted { get; set; }

    /// <summary>
    /// Correlation ID for tracing
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Status message
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp of processing
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static PupilResponse Success(string correlationId, string message = "Message accepted") => new()
    {
        Accepted = true,
        CorrelationId = correlationId,
        Message = message
    };

    public static PupilResponse Error(string correlationId, string message) => new()
    {
        Accepted = false,
        CorrelationId = correlationId,
        Message = message
    };
}

/// <summary>
/// Recovery data saved to file when NOC send fails after retries
/// </summary>
public class RecoveryData
{
    /// <summary>
    /// Version of the recovery data format
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When the failure occurred
    /// </summary>
    public DateTime FailedAt { get; set; }

    /// <summary>
    /// Original correlation ID
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Source context (e.g., "watchdog", "pupil")
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Reason for the failure
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// NOC details to send on recovery
    /// </summary>
    public NocDetails NocDetails { get; set; } = new();

    /// <summary>
    /// When recovery was attempted (set on startup)
    /// </summary>
    public DateTime? RecoveredAt { get; set; }
}

