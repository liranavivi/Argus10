using Argus.Models;

namespace Argus.Services.Metrics;

/// <summary>
/// Interface for Argus system metrics.
/// Tracks ingestion, lifecycle, NOC decisions, and infrastructure health.
/// </summary>
public interface IArgusMetrics
{
    #region Ingestion Metrics

    /// <summary>
    /// Increment total alerts received counter
    /// </summary>
    /// <param name="source">Alert source: k8s_layer, prometheus_push, watchdog</param>
    void IncrementAlertsReceived(string source);

    /// <summary>
    /// Increment filtered alerts counter (no platform=argus label)
    /// </summary>
    void IncrementAlertsFiltered();

    #endregion

    #region Alert Lifecycle Metrics

    /// <summary>
    /// Increment alerts that entered CREATE status
    /// </summary>
    void IncrementAlertsCreated();

    /// <summary>
    /// Increment alerts that entered UNKNOWN status
    /// </summary>
    void IncrementAlertsUnknown();

    /// <summary>
    /// Increment alerts that were resolved (CANCEL processed)
    /// </summary>
    void IncrementAlertsResolved();

    #endregion

    #region Alert Vector State

    /// <summary>
    /// Update current alerts vector size gauge
    /// </summary>
    void SetAlertsVectorSize(int size);

    /// <summary>
    /// Update current alerts count by status gauge
    /// </summary>
    void SetAlertsVectorByStatus(AlertStatus status, int count);

    #endregion

    #region NOC Decision Metrics

    /// <summary>
    /// Increment NOC decisions enqueued by type
    /// </summary>
    void IncrementNocDecisions(NocDecisionType type);

    /// <summary>
    /// Increment successfully sent to NOC
    /// </summary>
    void IncrementNocSent();

    /// <summary>
    /// Increment suppressed by suppression cache
    /// </summary>
    void IncrementNocSuppressed();

    /// <summary>
    /// Update current NOC queue depth gauge
    /// </summary>
    void SetNocQueueDepth(int depth);

    #endregion

    #region Infrastructure Health Metrics

    /// <summary>
    /// Record K8s polling duration
    /// </summary>
    void RecordK8sPollDuration(TimeSpan duration);

    /// <summary>
    /// Record snapshot processing duration
    /// </summary>
    void RecordSnapshotDuration(TimeSpan duration);

    /// <summary>
    /// Update circuit breaker state gauge (0=Closed, 1=Open, 2=HalfOpen)
    /// </summary>
    void SetCircuitBreakerState(int state);

    /// <summary>
    /// Update grace period active gauge (1=active, 0=expired)
    /// </summary>
    void SetGracePeriodActive(bool active);

    #endregion

    #region L2 (Hazelcast) Persistence Metrics

    /// <summary>
    /// Record a successful L2 write
    /// </summary>
    void RecordL2WriteSuccess();

    /// <summary>
    /// Record a failed L2 write
    /// </summary>
    void RecordL2WriteFailure();

    /// <summary>
    /// Set L2 availability state
    /// </summary>
    void SetL2Available(bool available);

    #endregion

    #region Snapshot Accessors

    /// <summary>
    /// Get current metrics snapshot for reporting
    /// </summary>
    ArgusMetricsSnapshot GetSnapshot();

    /// <summary>
    /// Get metrics in Prometheus text exposition format
    /// </summary>
    string GetPrometheusMetrics();

    #endregion
}

/// <summary>
/// Snapshot of current metrics values for reporting
/// </summary>
public class ArgusMetricsSnapshot
{
    // Ingestion
    public long TotalAlertsReceived { get; set; }
    public long TotalAlertsFiltered { get; set; }
    public Dictionary<string, long> AlertsReceivedBySource { get; set; } = new();

    // Lifecycle
    public long TotalAlertsCreated { get; set; }
    public long TotalAlertsUnknown { get; set; }
    public long TotalAlertsResolved { get; set; }

    // Vector State
    public int AlertsVectorSize { get; set; }
    public Dictionary<AlertStatus, int> AlertsVectorByStatus { get; set; } = new();

    // NOC
    public long TotalNocDecisions { get; set; }
    public Dictionary<NocDecisionType, long> NocDecisionsByType { get; set; } = new();
    public long TotalNocSent { get; set; }
    public long TotalNocSuppressed { get; set; }
    public int NocQueueDepth { get; set; }

    // Health
    public int CircuitBreakerState { get; set; }
    public bool GracePeriodActive { get; set; }
    public TimeSpan LastK8sPollDuration { get; set; }
    public TimeSpan LastSnapshotDuration { get; set; }

    // L2 (Hazelcast) Persistence
    public long TotalL2WriteSuccesses { get; set; }
    public long TotalL2WriteFailures { get; set; }
    public bool L2Available { get; set; }
}
