namespace Argus.Models;

/// <summary>
/// Watchdog heartbeat state
/// </summary>
public class WatchdogState
{
    /// <summary>Whether watchdog is currently active (received within timeout)</summary>
    public bool Active { get; set; }
    
    /// <summary>Timestamp of last watchdog received</summary>
    public DateTime? LastReceivedAt { get; set; }
    
    /// <summary>Seconds since last watchdog was received</summary>
    public int SecondsSinceLast => LastReceivedAt.HasValue 
        ? (int)(DateTime.UtcNow - LastReceivedAt.Value).TotalSeconds 
        : -1;
    
    /// <summary>Current watchdog status</summary>
    public WatchdogStatus Status { get; set; } = WatchdogStatus.Initializing;
    
    /// <summary>Whether startup grace period is active</summary>
    public bool GracePeriodActive { get; set; } = true;
    
    /// <summary>Reason for current status</summary>
    public string StatusReason { get; set; } = string.Empty;
    
    /// <summary>Timestamp when this state was captured</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

