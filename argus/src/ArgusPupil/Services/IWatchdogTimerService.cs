using ArgusPupil.Models;

namespace ArgusPupil.Services;

/// <summary>
/// Watchdog timer state
/// </summary>
public class WatchdogTimerState
{
    /// <summary>
    /// Whether the watchdog is currently active (has received at least one heartbeat)
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Whether the watchdog has expired
    /// </summary>
    public bool IsExpired { get; set; }

    /// <summary>
    /// Whether the grace period is still active
    /// </summary>
    public bool IsGracePeriodActive { get; set; }

    /// <summary>
    /// Last heartbeat received timestamp
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// Current timeout in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Seconds since last heartbeat
    /// </summary>
    public int SecondsSinceLastHeartbeat => LastHeartbeat.HasValue
        ? (int)(DateTime.UtcNow - LastHeartbeat.Value).TotalSeconds
        : -1;
}

/// <summary>
/// Service for managing watchdog timer
/// </summary>
public interface IWatchdogTimerService : IDisposable
{
    /// <summary>
    /// Process a heartbeat message - resets the timer
    /// </summary>
    /// <param name="message">The heartbeat message</param>
    void ProcessHeartbeat(HeartbeatMessage message);

    /// <summary>
    /// Get current watchdog state
    /// </summary>
    WatchdogTimerState GetState();

    /// <summary>
    /// Get the last NOC details from the most recent heartbeat
    /// </summary>
    NocDetails? GetLastNocDetails();

    /// <summary>
    /// Start the watchdog timer (called on startup)
    /// </summary>
    void Start();

    /// <summary>
    /// Stop the watchdog timer
    /// </summary>
    void Stop();
}

