namespace Argus.Configuration;

/// <summary>
/// Configuration for ArgusCoordinator
/// </summary>
public class CoordinatorConfiguration
{
    /// <summary>
    /// Interval in seconds for taking NOC snapshots.
    /// Default: 30 seconds
    /// </summary>
    public int SnapshotIntervalSeconds { get; set; } = 30;
}

