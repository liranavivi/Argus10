using Argus.Configuration;
using Argus.Models;
using Argus.Services.Metrics;
using Argus.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.AlertsVector;

/// <summary>
/// Service to manage the real-time alerts vector.
/// The vector is ordered by priority (lowest value = highest priority).
/// Each priority level can contain multiple alerts.
/// All alert sources (K8s layer, Prometheus, Watchdog) update this vector asynchronously.
///
/// Two-tier architecture:
/// - L1 (Memory): Primary read/write layer, all operations go through L1 first
/// - L2 (Hazelcast): Distributed cache, survives pod crash, write-behind from L1
/// </summary>
public interface IAlertsVectorService
{
    /// <summary>
    /// Update or add an alert to the vector
    /// </summary>
    void UpdateAlert(AlertDto alert);

    /// <summary>
    /// Remove an alert from the vector by fingerprint
    /// </summary>
    /// <returns>True if alert was removed, false if not found</returns>
    bool RemoveAlert(string fingerprint);

    /// <summary>
    /// Get a snapshot of the current alerts vector ordered by priority
    /// </summary>
    List<AlertDto> GetSnapshot();

    /// <summary>
    /// Get count of active (CREATE status) alerts
    /// </summary>
    int GetActiveAlertCount();

    /// <summary>
    /// Remove stale CREATE alerts that have exceeded the TTL.
    /// Returns the number of alerts removed.
    /// </summary>
    int CleanupExpiredAlerts();

    /// <summary>
    /// Clear all alerts (for testing)
    /// </summary>
    void Clear();

    /// <summary>
    /// Initialize the alerts vector from L2 (Hazelcast) storage.
    /// Called on startup to recover state after crash.
    /// </summary>
    /// <returns>True if data was loaded from L2 (crash recovery mode), false if fresh start</returns>
    Task<bool> InitializeFromL2Async();

    /// <summary>
    /// Whether this instance was started in crash recovery mode
    /// (i.e., state was loaded from L2 on startup)
    /// </summary>
    bool IsCrashRecovery { get; }

    /// <summary>
    /// Get pending changes for L2 synchronization.
    /// Returns dirty alerts to save and fingerprints to remove.
    /// </summary>
    (Dictionary<string, AlertDto> DirtyAlerts, List<string> RemovedFingerprints) GetPendingChanges();

    /// <summary>
    /// Clear dirty flags for alerts that have been successfully synced to L2.
    /// </summary>
    void ClearDirtyFlags(IEnumerable<string> fingerprints);

    /// <summary>
    /// Clear removed flags for fingerprints that have been successfully removed from L2.
    /// </summary>
    void ClearRemovedFlags(IEnumerable<string> fingerprints);
}

public class AlertsVectorService : IAlertsVectorService
{
    private readonly ILogger<AlertsVectorService> _logger;
    private readonly IArgusMetrics _metrics;
    private readonly IAlertsPersistenceService _persistence;
    private readonly AlertsVectorConfiguration _config;
    private readonly TimeSpan _alertTtl;

    // L1 (Memory) storage
    private readonly Dictionary<string, AlertDto> _alerts = new();
    private readonly object _lock = new();

    // L2 synchronization tracking
    private readonly HashSet<string> _dirtyFingerprints = new();
    private readonly HashSet<string> _removedFingerprints = new();

    // Crash recovery state
    private bool _isCrashRecovery;

    public AlertsVectorService(
        ILogger<AlertsVectorService> logger,
        IArgusMetrics metrics,
        IAlertsPersistenceService persistence,
        IOptions<AlertsVectorConfiguration> config)
    {
        _logger = logger;
        _metrics = metrics;
        _persistence = persistence;
        _config = config.Value;
        _alertTtl = TimeSpanParser.ParseToTimeSpan(_config.AlertTtl);

        _logger.LogInformation("AlertsVectorService initialized with TTL={Ttl}", _config.AlertTtl);
    }

    /// <inheritdoc />
    public bool IsCrashRecovery
    {
        get { lock (_lock) return _isCrashRecovery; }
        private set { lock (_lock) _isCrashRecovery = value; }
    }

    /// <inheritdoc />
    public async Task<bool> InitializeFromL2Async()
    {
        try
        {
            var alerts = await _persistence.LoadAllAsync();

            if (alerts.Count == 0)
            {
                _logger.LogInformation("No alerts loaded from L2. Starting in fresh start mode");
                IsCrashRecovery = false;
                return false;
            }

            lock (_lock)
            {
                foreach (var kvp in alerts)
                {
                    _alerts[kvp.Key] = kvp.Value;
                }
                _isCrashRecovery = true;
            }

            _logger.LogInformation(
                "Loaded {Count} alerts from L2. Starting in crash recovery mode",
                alerts.Count);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load from L2. Starting in fresh start mode");
            IsCrashRecovery = false;
            return false;
        }
    }

    /// <inheritdoc />
    public void UpdateAlert(AlertDto alert)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(alert.Fingerprint))
            {
                _logger.LogWarning("Alert {Name} has no fingerprint, skipping", alert.Name);
                return;
            }

            // Check if this is a new alert or status change for lifecycle metrics
            var isNew = !_alerts.TryGetValue(alert.Fingerprint, out var existingAlert);
            var statusChanged = !isNew && existingAlert!.Status != alert.Status;

            // If CANCEL arrives for an alert not in the vector, ignore it silently
            // This happens when Prometheus sends resolved notifications for alerts that:
            // - Fired before this Argus instance started
            // - Were already processed and removed from the vector
            if (isNew && alert.Status == AlertStatus.CANCEL)
            {
                _logger.LogDebug(
                    "Ignoring CANCEL for unknown alert: {Name} (Fingerprint={Fingerprint}, Source={Source}) - alert was not in vector",
                    alert.Name, alert.Fingerprint, alert.Source);
                return;
            }

            // Update LastSeen timestamp
            alert.LastSeen = DateTime.UtcNow;

            // Track lifecycle metrics for new alerts or status changes
            if (isNew || statusChanged)
            {
                switch (alert.Status)
                {
                    case AlertStatus.CREATE:
                        _metrics.IncrementAlertsCreated();
                        break;
                    case AlertStatus.UNKNOWN:
                        _metrics.IncrementAlertsUnknown();
                        break;
                }
            }

            // Update or add the alert
            _alerts[alert.Fingerprint] = alert;

            // Mark as dirty for L2 sync
            _dirtyFingerprints.Add(alert.Fingerprint);
            _removedFingerprints.Remove(alert.Fingerprint); // In case it was pending removal

            _logger.LogDebug(
                "Alert vector updated: {Status} {Name} (Priority={Priority}, Fingerprint={Fingerprint}, Source={Source}, IsNew={IsNew})",
                alert.Status, alert.Name, alert.Priority, alert.Fingerprint, alert.Source, isNew);
        }
    }

    /// <inheritdoc />
    public bool RemoveAlert(string fingerprint)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(fingerprint))
            {
                _logger.LogWarning("Cannot remove alert: fingerprint is empty");
                return false;
            }

            if (_alerts.TryGetValue(fingerprint, out var alert))
            {
                _alerts.Remove(fingerprint);

                // Track resolved metrics (alert removed = resolved)
                _metrics.IncrementAlertsResolved();

                // Mark for L2 removal
                _removedFingerprints.Add(fingerprint);
                _dirtyFingerprints.Remove(fingerprint); // No need to sync if being removed

                _logger.LogDebug(
                    "Alert removed from vector: {Name} (Priority={Priority}, Fingerprint={Fingerprint}, Source={Source})",
                    alert.Name, alert.Priority, alert.Fingerprint, alert.Source);
                return true;
            }

            return false;
        }
    }

    /// <inheritdoc />
    public List<AlertDto> GetSnapshot()
    {
        lock (_lock)
        {
            // Return alerts ordered by priority (lowest value first = highest priority)
            // Then by timestamp (oldest first)
            return _alerts.Values
                .OrderBy(a => a.Priority)
                .ThenBy(a => a.Timestamp)
                .ToList();
        }
    }

    /// <inheritdoc />
    public int GetActiveAlertCount()
    {
        lock (_lock)
        {
            return _alerts.Values.Count(a => a.Status == AlertStatus.CREATE);
        }
    }

    /// <inheritdoc />
    public int CleanupExpiredAlerts()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var expiredAlerts = _alerts.Values
                .Where(a => a.Status == AlertStatus.CREATE &&
                            (now - a.LastSeen) > _alertTtl)
                .ToList();

            foreach (var alert in expiredAlerts)
            {
                _alerts.Remove(alert.Fingerprint);

                // Mark for L2 removal
                _removedFingerprints.Add(alert.Fingerprint);
                _dirtyFingerprints.Remove(alert.Fingerprint);

                _logger.LogWarning(
                    "Alert expired (TTL): {Name} (Priority={Priority}, Fingerprint={Fingerprint}, Source={Source}, LastSeen={LastSeen}, Age={Age})",
                    alert.Name, alert.Priority, alert.Fingerprint, alert.Source,
                    alert.LastSeen, now - alert.LastSeen);
            }

            if (expiredAlerts.Count > 0)
            {
                _logger.LogInformation(
                    "TTL cleanup completed: {Count} stale CREATE alert(s) removed",
                    expiredAlerts.Count);
            }

            return expiredAlerts.Count;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_lock)
        {
            // Track all current alerts for L2 removal
            foreach (var fingerprint in _alerts.Keys)
            {
                _removedFingerprints.Add(fingerprint);
            }

            _alerts.Clear();
            _dirtyFingerprints.Clear();
            _logger.LogDebug("Alerts vector cleared");
        }
    }

    /// <inheritdoc />
    public (Dictionary<string, AlertDto> DirtyAlerts, List<string> RemovedFingerprints) GetPendingChanges()
    {
        lock (_lock)
        {
            var dirtyAlerts = new Dictionary<string, AlertDto>();

            foreach (var fingerprint in _dirtyFingerprints)
            {
                if (_alerts.TryGetValue(fingerprint, out var alert))
                {
                    dirtyAlerts[fingerprint] = alert;
                }
            }

            var removedList = _removedFingerprints.ToList();

            return (dirtyAlerts, removedList);
        }
    }

    /// <inheritdoc />
    public void ClearDirtyFlags(IEnumerable<string> fingerprints)
    {
        lock (_lock)
        {
            foreach (var fingerprint in fingerprints)
            {
                _dirtyFingerprints.Remove(fingerprint);
            }
        }
    }

    /// <inheritdoc />
    public void ClearRemovedFlags(IEnumerable<string> fingerprints)
    {
        lock (_lock)
        {
            foreach (var fingerprint in fingerprints)
            {
                _removedFingerprints.Remove(fingerprint);
            }
        }
    }
}
