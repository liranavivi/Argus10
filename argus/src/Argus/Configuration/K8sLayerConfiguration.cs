namespace Argus.Configuration;

/// <summary>
/// K8s Layer configuration - Kubernetes Infrastructure State monitoring
/// </summary>
public class K8sLayerConfiguration
{
    public KubernetesConfiguration Kubernetes { get; set; } = new();
    public PodMonitorConfiguration PrometheusPod { get; set; } = new();
    public PodMonitorConfiguration KsmPod { get; set; } = new();
    public RetryConfiguration Retry { get; set; } = new();
    public CircuitBreakerConfiguration CircuitBreaker { get; set; } = new();
    public RestartTrackingConfiguration RestartTracking { get; set; } = new();
    public int PollingIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Kubernetes connection configuration
/// </summary>
public class KubernetesConfiguration
{
    public string Namespace { get; set; } = "argus";
    public int ApiTimeoutSeconds { get; set; } = 30;
    public bool UseInClusterConfig { get; set; } = true;
}

/// <summary>
/// NOC behavior configuration for a specific alert status
/// </summary>
public class NocBehaviorConfiguration
{
    /// <summary>Whether alerts should be sent to NOC</summary>
    public bool SendToNoc { get; set; } = true;

    /// <summary>Payload for NOC decisions</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Suppression window for alerts (e.g., "2m", "5m")</summary>
    public string SuppressWindow { get; set; } = "5m";
}

/// <summary>
/// Pod monitoring configuration with alert settings
/// </summary>
public class PodMonitorConfiguration
{
    public string LabelSelector { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>NOC behavior when pod is DOWN/UNSTABLE (AlertStatus.CREATE)</summary>
    public NocBehaviorConfiguration CreateNocBehavior { get; set; } = new();

    /// <summary>NOC behavior when K8s API is unavailable (AlertStatus.UNKNOWN)</summary>
    public NocBehaviorConfiguration UnknownNocBehavior { get; set; } = new();
}

/// <summary>
/// Retry policy configuration
/// </summary>
public class RetryConfiguration
{
    public int MaxRetries { get; set; } = 3;
    public int[] DelayMilliseconds { get; set; } = [1000, 2000, 4000];
}

/// <summary>
/// Circuit breaker configuration
/// </summary>
public class CircuitBreakerConfiguration
{
    public int FailureThreshold { get; set; } = 5;
    public int OpenDurationSeconds { get; set; } = 30;
    public int SuccessThreshold { get; set; } = 1;
}

/// <summary>
/// Restart tracking sliding window configuration
/// </summary>
public class RestartTrackingConfiguration
{
    public int WindowSize { get; set; } = 5;
    public int RestartThreshold { get; set; } = 3;

    /// <summary>
    /// Grace period in seconds during startup before restart storm detection is active.
    /// During crash recovery, restart tracking has no grace period (immediate detection).
    /// </summary>
    public int NormalGracePeriodSeconds { get; set; } = 300;
}

/// <summary>
/// Watchdog heartbeat configuration
/// </summary>
public class WatchdogConfiguration
{
    /// <summary>Name of the watchdog alert from Prometheus</summary>
    public string AlertName { get; set; } = "Watchdog";

    /// <summary>Timeout in seconds before watchdog is considered missing</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Grace period in seconds during normal startup (fresh start)
    /// </summary>
    public int NormalGracePeriodSeconds { get; set; } = 180;

    /// <summary>
    /// Shorter grace period in seconds when recovering from crash (L2 data loaded).
    /// Default: 15 seconds
    /// </summary>
    public int CrashRecoveryGracePeriodSeconds { get; set; } = 15;

    /// <summary>Whether watchdog alerts should be sent to NOC</summary>
    public bool SendToNoc { get; set; } = true;

    /// <summary>Payload for NOC decisions when watchdog expires</summary>
    public string Payload { get; set; } = "component=prometheus,type=watchdog,severity=critical";

    /// <summary>Suppression window for watchdog alerts (e.g., "2m", "5m")</summary>
    public string SuppressWindow { get; set; } = "2m";
}
