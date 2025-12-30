using System.Text.Json.Serialization;

namespace ArgusPupil.Models;

/// <summary>
/// Type of message received by ArgusPupil
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PupilMessageType
{
    /// <summary>
    /// Heartbeat message - resets watchdog timer
    /// </summary>
    Heartbeat,

    /// <summary>
    /// Send NOC message immediately
    /// </summary>
    SendNocMessage
}

/// <summary>
/// Base class for all pupil messages
/// </summary>
public abstract class PupilMessageBase
{
    /// <summary>
    /// Type of message
    /// </summary>
    public abstract PupilMessageType MessageType { get; }

    /// <summary>
    /// Correlation ID for tracing
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the message was created
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Details to send to NOC service
/// </summary>
public class NocDetails
{
    /// <summary>
    /// Alert priority (lower = higher priority). Default: 1
    /// </summary>
    public int Priority { get; set; } = 1;

    /// <summary>
    /// Alert name/identifier
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Short summary of the alert
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Additional payload/context (e.g., "component=myapp,severity=critical")
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Source identifier (application name or composite key)
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Whether to send to NOC. Default: true
    /// </summary>
    public bool SendToNoc { get; set; } = true;

    /// <summary>
    /// Suppression window for deduplication (e.g., "5m", "1h")
    /// </summary>
    public string SuppressWindow { get; set; } = string.Empty;
}

/// <summary>
/// Heartbeat message - resets watchdog timer
/// </summary>
public class HeartbeatMessage : PupilMessageBase
{
    /// <inheritdoc />
    public override PupilMessageType MessageType => PupilMessageType.Heartbeat;

    /// <summary>
    /// NOC details to send if watchdog timer expires
    /// </summary>
    public NocDetails NocDetails { get; set; } = new();

    /// <summary>
    /// Optional override for watchdog timeout in seconds.
    /// If null, uses configured default timeout.
    /// </summary>
    public int? TimeoutSeconds { get; set; }
}

/// <summary>
/// Send NOC message immediately
/// </summary>
public class SendNocMessageCommand : PupilMessageBase
{
    /// <inheritdoc />
    public override PupilMessageType MessageType => PupilMessageType.SendNocMessage;

    /// <summary>
    /// NOC details to send immediately
    /// </summary>
    public NocDetails NocDetails { get; set; } = new();
}

