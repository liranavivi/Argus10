namespace Argus.Configuration;

/// <summary>
/// NOC (Network Operations Center) configuration
/// Supports time duration formats: s (seconds), m (minutes), h (hours), d (days)
/// Examples: "120s", "4m", "8h", "3d"
/// </summary>
public class NocConfiguration
{
    /// <summary>
    /// Default suppression window if not specified in alert annotations or config.
    /// Used as fallback for invalid/missing suppress_window values.
    /// Format: &lt;number&gt;&lt;unit&gt; where unit is s, m, h, or d (e.g., "10m", "1h")
    /// </summary>
    public string DefaultWindow { get; set; } = "10m"; // 10 minutes default

    /// <summary>
    /// Interval for cleaning up old suppression cache entries.
    /// Format: &lt;number&gt;&lt;unit&gt; where unit is s, m, h, or d (e.g., "15m", "1h")
    /// </summary>
    public string CleanupInterval { get; set; } = "15m"; // 15 minutes

    /// <summary>
    /// Window for deduplicating alerts before enqueueing to NOC queue.
    /// Prevents the same alert from being enqueued multiple times within this window.
    /// Format: &lt;number&gt;&lt;unit&gt; where unit is s, m, h, or d (e.g., "60s", "2m")
    /// </summary>
    public string DuplicateWindow { get; set; } = "60s"; // 60 seconds default
}

