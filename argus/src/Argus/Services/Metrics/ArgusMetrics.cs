using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Argus.Models;

namespace Argus.Services.Metrics;

/// <summary>
/// OpenTelemetry-based implementation of Argus system metrics.
/// Uses System.Diagnostics.Metrics for proper OTel integration.
/// All metrics are exported via OTel Collector with telemetry_source="argus_service".
/// </summary>
public class ArgusMetrics : IArgusMetrics
{
    /// <summary>
    /// The meter name used for Argus service metrics.
    /// Must match the name registered with AddMeter() in Program.cs.
    /// </summary>
    public const string MeterName = "Argus.Service";

    private readonly Meter _meter;

    // Ingestion metrics
    private readonly Counter<long> _alertsReceivedCounter;
    private readonly Counter<long> _alertsFilteredCounter;
    private readonly ConcurrentDictionary<string, long> _alertsReceivedBySource = new();

    // Lifecycle metrics
    private readonly Counter<long> _alertsCreatedCounter;
    private readonly Counter<long> _alertsUnknownCounter;
    private readonly Counter<long> _alertsResolvedCounter;

    // Vector state gauges (ObservableGauge callbacks)
    private int _alertsVectorSize;
    private readonly ConcurrentDictionary<AlertStatus, int> _alertsVectorByStatus = new();

    // NOC metrics
    private readonly Counter<long> _nocDecisionsCounter;
    private readonly ConcurrentDictionary<NocDecisionType, long> _nocDecisionsByType = new();
    private readonly Counter<long> _nocSentCounter;
    private readonly Counter<long> _nocSuppressedCounter;
    private int _nocQueueDepth;

    // Health metrics
    private int _circuitBreakerState;
    private int _gracePeriodActive = 1; // Start as active
    private readonly Histogram<double> _k8sPollDurationHistogram;
    private readonly Histogram<double> _snapshotDurationHistogram;

    // L2 (Hazelcast) persistence metrics
    private readonly Counter<long> _l2WriteSuccessCounter;
    private readonly Counter<long> _l2WriteFailureCounter;
    private int _l2Available = 1; // Start as available
    private long _totalL2WriteSuccesses;
    private long _totalL2WriteFailures;

    // For snapshot reporting (backward compatibility)
    private long _totalAlertsReceived;
    private long _totalAlertsFiltered;
    private long _totalAlertsCreated;
    private long _totalAlertsUnknown;
    private long _totalAlertsResolved;
    private long _totalNocDecisions;
    private long _totalNocSent;
    private long _totalNocSuppressed;
    private TimeSpan _lastK8sPollDuration;
    private TimeSpan _lastSnapshotDuration;
    private readonly object _durationLock = new();

    public ArgusMetrics()
    {
        // Create meter for Argus service metrics
        // The meter name must match what's registered with AddMeter() in Program.cs
        _meter = new Meter(MeterName, "1.0.0");

        // Ingestion metrics
        _alertsReceivedCounter = _meter.CreateCounter<long>(
            "argus_alerts_received",
            unit: "count",
            description: "Total alerts received from all sources");

        _alertsFilteredCounter = _meter.CreateCounter<long>(
            "argus_alerts_filtered",
            unit: "count",
            description: "Alerts filtered (no platform=argus label)");

        // Lifecycle metrics
        _alertsCreatedCounter = _meter.CreateCounter<long>(
            "argus_alerts_created",
            unit: "count",
            description: "Alerts that entered CREATE status");

        _alertsUnknownCounter = _meter.CreateCounter<long>(
            "argus_alerts_unknown",
            unit: "count",
            description: "Alerts that entered UNKNOWN status");

        _alertsResolvedCounter = _meter.CreateCounter<long>(
            "argus_alerts_resolved",
            unit: "count",
            description: "Alerts resolved (CANCEL processed)");

        // Vector state gauges (ObservableGauge)
        _meter.CreateObservableGauge(
            "argus_alerts_vector_size",
            () => _alertsVectorSize,
            unit: "count",
            description: "Current alerts vector size");

        _meter.CreateObservableGauge(
            "argus_alerts_vector_by_status",
            () => _alertsVectorByStatus.Select(kvp => new Measurement<int>(
                kvp.Value,
                new KeyValuePair<string, object?>("status", kvp.Key.ToString()))),
            unit: "count",
            description: "Alerts by status");

        // NOC metrics
        _nocDecisionsCounter = _meter.CreateCounter<long>(
            "argus_noc_decisions",
            unit: "count",
            description: "NOC decisions enqueued");

        _nocSentCounter = _meter.CreateCounter<long>(
            "argus_noc_sent",
            unit: "count",
            description: "Alerts sent to NOC");

        _nocSuppressedCounter = _meter.CreateCounter<long>(
            "argus_noc_suppressed",
            unit: "count",
            description: "Alerts suppressed by cache");

        _meter.CreateObservableGauge(
            "argus_noc_queue_depth",
            () => _nocQueueDepth,
            unit: "count",
            description: "Current NOC queue depth");

        // Health metrics
        _k8sPollDurationHistogram = _meter.CreateHistogram<double>(
            "argus_k8s_poll_duration",
            unit: "s",
            description: "K8s polling duration");

        _snapshotDurationHistogram = _meter.CreateHistogram<double>(
            "argus_snapshot_duration",
            unit: "s",
            description: "Snapshot processing duration");

        _meter.CreateObservableGauge(
            "argus_circuit_breaker_state",
            () => _circuitBreakerState,
            unit: "state",
            description: "Circuit breaker state (0=Closed, 1=Open, 2=HalfOpen)");

        _meter.CreateObservableGauge(
            "argus_grace_period_active",
            () => _gracePeriodActive,
            unit: "state",
            description: "Grace period active (1=active, 0=expired)");

        // L2 (Hazelcast) persistence metrics
        _l2WriteSuccessCounter = _meter.CreateCounter<long>(
            "argus_l2_write_success",
            unit: "count",
            description: "Successful L2 (Hazelcast) writes");

        _l2WriteFailureCounter = _meter.CreateCounter<long>(
            "argus_l2_write_failure",
            unit: "count",
            description: "Failed L2 (Hazelcast) writes");

        _meter.CreateObservableGauge(
            "argus_l2_available",
            () => _l2Available,
            unit: "state",
            description: "L2 (Hazelcast) availability (1=available, 0=unavailable)");
    }

    #region Ingestion Metrics

    public void IncrementAlertsReceived(string source)
    {
        _alertsReceivedCounter.Add(1, new KeyValuePair<string, object?>("source", source));
        _alertsReceivedBySource.AddOrUpdate(source, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _totalAlertsReceived);
    }

    public void IncrementAlertsFiltered()
    {
        _alertsFilteredCounter.Add(1);
        Interlocked.Increment(ref _totalAlertsFiltered);
    }

    #endregion

    #region Alert Lifecycle Metrics

    public void IncrementAlertsCreated()
    {
        _alertsCreatedCounter.Add(1);
        Interlocked.Increment(ref _totalAlertsCreated);
    }

    public void IncrementAlertsUnknown()
    {
        _alertsUnknownCounter.Add(1);
        Interlocked.Increment(ref _totalAlertsUnknown);
    }

    public void IncrementAlertsResolved()
    {
        _alertsResolvedCounter.Add(1);
        Interlocked.Increment(ref _totalAlertsResolved);
    }

    #endregion

    #region Alert Vector State

    public void SetAlertsVectorSize(int size)
    {
        Interlocked.Exchange(ref _alertsVectorSize, size);
    }

    public void SetAlertsVectorByStatus(AlertStatus status, int count)
    {
        _alertsVectorByStatus[status] = count;
    }

    #endregion

    #region NOC Decision Metrics

    public void IncrementNocDecisions(NocDecisionType type)
    {
        _nocDecisionsCounter.Add(1, new KeyValuePair<string, object?>("type", type.ToString()));
        _nocDecisionsByType.AddOrUpdate(type, 1, (_, count) => count + 1);
        Interlocked.Increment(ref _totalNocDecisions);
    }

    public void IncrementNocSent()
    {
        _nocSentCounter.Add(1);
        Interlocked.Increment(ref _totalNocSent);
    }

    public void IncrementNocSuppressed()
    {
        _nocSuppressedCounter.Add(1);
        Interlocked.Increment(ref _totalNocSuppressed);
    }

    public void SetNocQueueDepth(int depth)
    {
        Interlocked.Exchange(ref _nocQueueDepth, depth);
    }

    #endregion

    #region Infrastructure Health Metrics

    public void RecordK8sPollDuration(TimeSpan duration)
    {
        _k8sPollDurationHistogram.Record(duration.TotalSeconds);
        lock (_durationLock)
        {
            _lastK8sPollDuration = duration;
        }
    }

    public void RecordSnapshotDuration(TimeSpan duration)
    {
        _snapshotDurationHistogram.Record(duration.TotalSeconds);
        lock (_durationLock)
        {
            _lastSnapshotDuration = duration;
        }
    }

    public void SetCircuitBreakerState(int state)
    {
        Interlocked.Exchange(ref _circuitBreakerState, state);
    }

    public void SetGracePeriodActive(bool active)
    {
        Interlocked.Exchange(ref _gracePeriodActive, active ? 1 : 0);
    }

    #endregion

    #region L2 (Hazelcast) Persistence Metrics

    public void RecordL2WriteSuccess()
    {
        _l2WriteSuccessCounter.Add(1);
        Interlocked.Increment(ref _totalL2WriteSuccesses);
    }

    public void RecordL2WriteFailure()
    {
        _l2WriteFailureCounter.Add(1);
        Interlocked.Increment(ref _totalL2WriteFailures);
    }

    public void SetL2Available(bool available)
    {
        Interlocked.Exchange(ref _l2Available, available ? 1 : 0);
    }

    #endregion

    #region Snapshot Accessors

    public ArgusMetricsSnapshot GetSnapshot()
    {
        lock (_durationLock)
        {
            return new ArgusMetricsSnapshot
            {
                // Ingestion
                TotalAlertsReceived = Interlocked.Read(ref _totalAlertsReceived),
                TotalAlertsFiltered = Interlocked.Read(ref _totalAlertsFiltered),
                AlertsReceivedBySource = new Dictionary<string, long>(_alertsReceivedBySource),

                // Lifecycle
                TotalAlertsCreated = Interlocked.Read(ref _totalAlertsCreated),
                TotalAlertsUnknown = Interlocked.Read(ref _totalAlertsUnknown),
                TotalAlertsResolved = Interlocked.Read(ref _totalAlertsResolved),

                // Vector State
                AlertsVectorSize = _alertsVectorSize,
                AlertsVectorByStatus = new Dictionary<AlertStatus, int>(_alertsVectorByStatus),

                // NOC
                TotalNocDecisions = Interlocked.Read(ref _totalNocDecisions),
                NocDecisionsByType = new Dictionary<NocDecisionType, long>(_nocDecisionsByType),
                TotalNocSent = Interlocked.Read(ref _totalNocSent),
                TotalNocSuppressed = Interlocked.Read(ref _totalNocSuppressed),
                NocQueueDepth = _nocQueueDepth,

                // Health
                CircuitBreakerState = _circuitBreakerState,
                GracePeriodActive = _gracePeriodActive == 1,
                LastK8sPollDuration = _lastK8sPollDuration,
                LastSnapshotDuration = _lastSnapshotDuration,

                // L2 (Hazelcast) Persistence
                TotalL2WriteSuccesses = Interlocked.Read(ref _totalL2WriteSuccesses),
                TotalL2WriteFailures = Interlocked.Read(ref _totalL2WriteFailures),
                L2Available = _l2Available == 1
            };
        }
    }

    public string GetPrometheusMetrics()
    {
        // This method is deprecated - metrics are now exported via OpenTelemetry
        // Kept for backward compatibility with /metrics endpoint
        return "# Metrics are now exported via OpenTelemetry to the OTel Collector\n" +
               "# Please scrape metrics from Prometheus instead of this endpoint\n";
    }

    #endregion
}

