using Argus.Configuration;
using Argus.Models;
using Microsoft.Extensions.Options;

namespace Argus.Services.AlertsVector;

/// <summary>
/// Background service that batches L1 changes and writes to L2 (Hazelcast) periodically.
/// Implements write-behind pattern with configurable batch window (default 100ms).
/// </summary>
public class AlertsVectorBatchWriter : BackgroundService
{
    private readonly IAlertsVectorService _alertsVector;
    private readonly IAlertsPersistenceService _persistence;
    private readonly ILogger<AlertsVectorBatchWriter> _logger;
    private readonly HazelcastSettings _settings;

    public AlertsVectorBatchWriter(
        IAlertsVectorService alertsVector,
        IAlertsPersistenceService persistence,
        ILogger<AlertsVectorBatchWriter> logger,
        IOptions<HazelcastSettings> settings)
    {
        _alertsVector = alertsVector;
        _persistence = persistence;
        _logger = logger;
        _settings = settings.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AlertsVectorBatchWriter started. BatchWindow={BatchWindowMs}ms",
            _settings.BatchWindowMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_settings.BatchWindowMs, stoppingToken);
                await SyncToL2Async();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch writer cycle");
            }
        }

        // Final sync before shutdown
        _logger.LogInformation("AlertsVectorBatchWriter stopping. Performing final sync...");
        await SyncToL2Async();
        _logger.LogInformation("AlertsVectorBatchWriter stopped");
    }

    private async Task SyncToL2Async()
    {
        // Get dirty alerts and removed fingerprints from L1
        var (dirtyAlerts, removedFingerprints) = _alertsVector.GetPendingChanges();

        if (dirtyAlerts.Count == 0 && removedFingerprints.Count == 0)
        {
            return;
        }

        // Save dirty alerts
        if (dirtyAlerts.Count > 0)
        {
            var saveSuccess = await _persistence.SaveBatchAsync(dirtyAlerts);
            if (saveSuccess)
            {
                _alertsVector.ClearDirtyFlags(dirtyAlerts.Keys);
                _logger.LogDebug("Synced {Count} alerts to L2", dirtyAlerts.Count);
            }
        }

        // Remove deleted alerts
        if (removedFingerprints.Count > 0)
        {
            var removeSuccess = await _persistence.RemoveBatchAsync(removedFingerprints);
            if (removeSuccess)
            {
                _alertsVector.ClearRemovedFlags(removedFingerprints);
                _logger.LogDebug("Removed {Count} alerts from L2", removedFingerprints.Count);
            }
        }
    }
}

