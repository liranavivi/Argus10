using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.Metrics;
using Microsoft.Extensions.Logging;

namespace Argus.Services.Noc;

/// <summary>
/// Service that takes snapshots of the alerts vector every 30 seconds
/// and enqueues decisions for asynchronous processing.
///
/// Enqueue Strategy:
/// - First CREATE alert (highest priority active alert)
/// - First UNKNOWN alert (highest priority unknown alert)
/// - All CANCEL alerts (regardless of priority)
/// </summary>
public interface INocSnapshotService
{
    /// <summary>
    /// Take a snapshot of the alerts vector and enqueue decisions
    /// </summary>
    void TakeSnapshot(string correlationId);

    /// <summary>
    /// Take a crash recovery snapshot - skips all CREATEs and enqueues only CANCELs.
    /// Used on startup after crash to clear stale alerts from NOC.
    /// </summary>
    void TakeCrashRecoverySnapshot(string correlationId);
}

public class NocSnapshotService : INocSnapshotService
{
    private readonly ILogger<NocSnapshotService> _logger;
    private readonly IAlertsVectorService _alertsVector;
    private readonly INocQueueService _nocQueue;
    private readonly IArgusMetrics _metrics;

    public NocSnapshotService(
        ILogger<NocSnapshotService> logger,
        IAlertsVectorService alertsVector,
        INocQueueService nocQueue,
        IArgusMetrics metrics)
    {
        _logger = logger;
        _alertsVector = alertsVector;
        _nocQueue = nocQueue;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public void TakeSnapshot(string correlationId)
    {
        var startTime = DateTime.UtcNow;

        // Clean up stale CREATE alerts before taking snapshot
        _alertsVector.CleanupExpiredAlerts();

        var snapshot = _alertsVector.GetSnapshot();

        // Count alerts by status
        var createCount = snapshot.Count(a => a.Status == AlertStatus.CREATE);
        var cancelCount = snapshot.Count(a => a.Status == AlertStatus.CANCEL);
        var unknownCount = snapshot.Count(a => a.Status == AlertStatus.UNKNOWN);
        var ignoreCount = snapshot.Count(a => a.Status == AlertStatus.IGNORE);

        // Update vector state metrics
        _metrics.SetAlertsVectorSize(snapshot.Count);
        _metrics.SetAlertsVectorByStatus(AlertStatus.CREATE, createCount);
        _metrics.SetAlertsVectorByStatus(AlertStatus.CANCEL, cancelCount);
        _metrics.SetAlertsVectorByStatus(AlertStatus.UNKNOWN, unknownCount);
        _metrics.SetAlertsVectorByStatus(AlertStatus.IGNORE, ignoreCount);
        _metrics.SetNocQueueDepth(_nocQueue.GetQueueDepth());

        // Log snapshot summary
        _logger.LogDebug(
            "NOC Snapshot: {Total} alerts ({Create} CREATE, {Cancel} CANCEL, {Unknown} UNKNOWN), Queue depth: {QueueDepth}. CorrelationId={CorrelationId}",
            snapshot.Count, createCount, cancelCount, unknownCount, _nocQueue.GetQueueDepth(), correlationId);

        // DEBUG: Log each alert in priority order
        for (int i = 0; i < snapshot.Count; i++)
        {
            var alert = snapshot[i];
            _logger.LogDebug(
                "  [{Index}] Priority={Priority} Status={Status} Name={Name} Summary={Summary} Fingerprint={Fingerprint} ExecutionId={ExecutionId}",
                i, alert.Priority, alert.Status, alert.Name, alert.Summary, alert.Fingerprint, alert.ExecutionId);
        }

        // Enqueue decisions
        EnqueueDecisions(snapshot, correlationId);

        // Record snapshot duration
        _metrics.RecordSnapshotDuration(DateTime.UtcNow - startTime);
    }

    /// <inheritdoc />
    public void TakeCrashRecoverySnapshot(string correlationId)
    {
        var startTime = DateTime.UtcNow;

        // Get current snapshot
        var snapshot = _alertsVector.GetSnapshot();

        // Count alerts by status
        var createCount = snapshot.Count(a => a.Status == AlertStatus.CREATE);
        var cancelCount = snapshot.Count(a => a.Status == AlertStatus.CANCEL);
        var unknownCount = snapshot.Count(a => a.Status == AlertStatus.UNKNOWN);

        _logger.LogInformation(
            "CRASH RECOVERY Snapshot: {Total} alerts ({Create} CREATE-SKIP, {Cancel} CANCEL, {Unknown} UNKNOWN-SKIP). " +
            "Discarding all CREATEs, enqueuing all CANCELs. CorrelationId={CorrelationId}",
            snapshot.Count, createCount, cancelCount, unknownCount, correlationId);

        // Convert all alerts to CANCEL status for crash recovery
        // This ensures NOC clears all stale alerts
        var allAlertsAsCancels = snapshot
            .Where(a => a.Status != AlertStatus.IGNORE)
            .Select(a => new AlertDto
            {
                Name = a.Name,
                Fingerprint = a.Fingerprint,
                Priority = a.Priority,
                Status = AlertStatus.CANCEL, // Force all to CANCEL
                Summary = $"[CRASH RECOVERY] {a.Summary}",
                Source = a.Source,
                SendToNoc = a.SendToNoc,
                Payload = a.Payload,
                SuppressWindow = a.SuppressWindow,
                LastSeen = a.LastSeen,
                ExecutionId = a.ExecutionId
            })
            .ToList();

        // Enqueue all as CANCELs (batch them together)
        if (allAlertsAsCancels.Any())
        {
            _nocQueue.Enqueue(new NocDecision
            {
                Type = NocDecisionType.HandleCancels,
                Alerts = allAlertsAsCancels,
                SnapshotTime = DateTime.UtcNow,
                CorrelationId = correlationId
            });

            foreach (var cancel in allAlertsAsCancels)
            {
                _nocQueue.MarkAsEnqueued(cancel.Fingerprint);
            }

            _logger.LogInformation(
                "CRASH RECOVERY: Enqueued {Count} CANCEL alerts to clear NOC. CorrelationId={CorrelationId}",
                allAlertsAsCancels.Count, correlationId);
        }
        else
        {
            _logger.LogInformation(
                "CRASH RECOVERY: No alerts to cancel. CorrelationId={CorrelationId}",
                correlationId);
        }

        // Record snapshot duration
        _metrics.RecordSnapshotDuration(DateTime.UtcNow - startTime);
    }

    private void EnqueueDecisions(List<AlertDto> alerts, string correlationId)
    {
        // Find first CREATE alert (highest priority active alert)
        var firstCreate = alerts.FirstOrDefault(a => a.Status == AlertStatus.CREATE);

        // Find first UNKNOWN alert (highest priority unknown alert)
        var firstUnknown = alerts.FirstOrDefault(a => a.Status == AlertStatus.UNKNOWN);

        // Find all CANCEL alerts
        var allCancels = alerts.Where(a => a.Status == AlertStatus.CANCEL).ToList();

        // Enqueue first CREATE (if exists and not already enqueued recently)
        if (firstCreate != null)
        {
            if (!_nocQueue.WasRecentlyEnqueued(firstCreate.Fingerprint))
            {
                _nocQueue.Enqueue(new NocDecision
                {
                    Type = NocDecisionType.HandleCreate,
                    Alert = firstCreate,
                    SnapshotTime = DateTime.UtcNow,
                    CorrelationId = correlationId
                });

                _nocQueue.MarkAsEnqueued(firstCreate.Fingerprint);

                _logger.LogDebug(
                    "Enqueued CREATE alert: {Name} (Priority={Priority}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    firstCreate.Name, firstCreate.Priority, correlationId, firstCreate.ExecutionId);
            }
            else
            {
                _logger.LogDebug(
                    "Skipped CREATE alert {Name} - recently enqueued. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    firstCreate.Name, correlationId, firstCreate.ExecutionId);
            }
        }

        // Enqueue first UNKNOWN (if exists and not already enqueued recently)
        if (firstUnknown != null)
        {
            if (!_nocQueue.WasRecentlyEnqueued(firstUnknown.Fingerprint))
            {
                _nocQueue.Enqueue(new NocDecision
                {
                    Type = NocDecisionType.HandleUnknown,
                    Alert = firstUnknown,
                    SnapshotTime = DateTime.UtcNow,
                    CorrelationId = correlationId
                });

                _nocQueue.MarkAsEnqueued(firstUnknown.Fingerprint);

                _logger.LogDebug(
                    "Enqueued UNKNOWN alert: {Name} (Priority={Priority}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    firstUnknown.Name, firstUnknown.Priority, correlationId, firstUnknown.ExecutionId);
            }
            else
            {
                _logger.LogDebug(
                    "Skipped UNKNOWN alert {Name} - recently enqueued. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    firstUnknown.Name, correlationId, firstUnknown.ExecutionId);
            }
        }

        // Log if no active or unknown alerts
        if (firstCreate == null && firstUnknown == null)
        {
            _logger.LogDebug(
                "No active or unknown alerts. System is healthy. CorrelationId={CorrelationId}",
                correlationId);
        }

        // Enqueue all CANCEL alerts (batch them together)
        if (allCancels.Any())
        {
            // Filter out recently enqueued CANCELs
            var newCancels = allCancels
                .Where(a => !_nocQueue.WasRecentlyEnqueued(a.Fingerprint))
                .ToList();

            if (newCancels.Any())
            {
                _nocQueue.Enqueue(new NocDecision
                {
                    Type = NocDecisionType.HandleCancels,
                    Alerts = newCancels,
                    SnapshotTime = DateTime.UtcNow,
                    CorrelationId = correlationId
                });

                foreach (var cancel in newCancels)
                {
                    _nocQueue.MarkAsEnqueued(cancel.Fingerprint);
                }

                _logger.LogDebug(
                    "Enqueued {Count} CANCEL alerts. CorrelationId={CorrelationId}",
                    newCancels.Count, correlationId);
            }
            else
            {
                _logger.LogDebug(
                    "Skipped {Count} CANCEL alerts - all recently enqueued. CorrelationId={CorrelationId}",
                    allCancels.Count, correlationId);
            }
        }
    }
}

