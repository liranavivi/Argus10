using System.Collections.Concurrent;
using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.Metrics;
using Argus.Utilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.Noc;

/// <summary>
/// Service to manage NOC decision queue and process decisions asynchronously.
/// Prevents blocking snapshots when sending HTTP requests to NOC service.
/// </summary>
public interface INocQueueService
{
    /// <summary>
    /// Enqueue a NOC decision for processing
    /// </summary>
    void Enqueue(NocDecision decision);
    
    /// <summary>
    /// Get current queue depth
    /// </summary>
    int GetQueueDepth();
    
    /// <summary>
    /// Check if an alert was recently enqueued (within duplicate window)
    /// </summary>
    bool WasRecentlyEnqueued(string fingerprint);
    
    /// <summary>
    /// Mark an alert as enqueued
    /// </summary>
    void MarkAsEnqueued(string fingerprint);
}

public class NocQueueService : BackgroundService, INocQueueService
{
    private readonly ILogger<NocQueueService> _logger;
    private readonly IAlertsVectorService _alertsVector;
    private readonly ISuppressionCache _suppressionCache;
    private readonly IArgusMetrics _metrics;
    private readonly NocConfiguration _config;
    private readonly ConcurrentQueue<NocDecision> _queue = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentlyEnqueued = new();
    private readonly int _duplicateWindowSeconds;

    private DateTime _lastCleanup = DateTime.UtcNow;

    public NocQueueService(
        ILogger<NocQueueService> logger,
        IAlertsVectorService alertsVector,
        ISuppressionCache suppressionCache,
        IArgusMetrics metrics,
        IOptions<NocConfiguration> config)
    {
        _logger = logger;
        _alertsVector = alertsVector;
        _suppressionCache = suppressionCache;
        _metrics = metrics;
        _config = config.Value;
        _duplicateWindowSeconds = TimeSpanParser.ParseToSeconds(_config.DuplicateWindow);
    }

    /// <inheritdoc />
    public void Enqueue(NocDecision decision)
    {
        _queue.Enqueue(decision);

        // Track NOC decision metric
        _metrics.IncrementNocDecisions(decision.Type);

        _logger.LogDebug(
            "Enqueued NOC decision: Type={Type}, QueueDepth={Depth}. CorrelationId={CorrelationId}",
            decision.Type, _queue.Count, decision.CorrelationId);
    }
    
    /// <inheritdoc />
    public int GetQueueDepth() => _queue.Count;
    
    /// <inheritdoc />
    public bool WasRecentlyEnqueued(string fingerprint)
    {
        if (_recentlyEnqueued.TryGetValue(fingerprint, out var enqueuedAt))
        {
            var age = (DateTime.UtcNow - enqueuedAt).TotalSeconds;
            return age < _duplicateWindowSeconds;
        }
        return false;
    }
    
    /// <inheritdoc />
    public void MarkAsEnqueued(string fingerprint)
    {
        _recentlyEnqueued[fingerprint] = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Background worker that processes the queue
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("NOC Queue Service started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Cleanup old entries periodically
                var cleanupIntervalSeconds = TimeSpanParser.ParseToSeconds(_config.CleanupInterval);
                if ((DateTime.UtcNow - _lastCleanup).TotalSeconds > cleanupIntervalSeconds)
                {
                    CleanupRecentlyEnqueued();
                    _suppressionCache.Cleanup();
                    _lastCleanup = DateTime.UtcNow;
                }

                // Process queue
                if (_queue.TryDequeue(out var decision))
                {
                    await ProcessDecisionAsync(decision, stoppingToken);
                }
                else
                {
                    // No work, wait a bit
                    await Task.Delay(100, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in NOC queue worker");
                await Task.Delay(1000, stoppingToken); // Wait before retrying
            }
        }
        
        _logger.LogInformation("NOC Queue Service stopped");
    }
    
    private async Task ProcessDecisionAsync(NocDecision decision, CancellationToken cancellationToken)
    {
        try
        {
            switch (decision.Type)
            {
                case NocDecisionType.HandleCreate:
                    await HandleCreateAlertAsync(decision, cancellationToken);
                    break;
                case NocDecisionType.HandleCancels:
                    await HandleCancelAlertsAsync(decision, cancellationToken);
                    break;
                case NocDecisionType.HandleUnknown:
                    await HandleUnknownAlertAsync(decision, cancellationToken);
                    break;
                default:
                    _logger.LogWarning("Unknown NOC decision type: {Type}. CorrelationId={CorrelationId}",
                        decision.Type, decision.CorrelationId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing NOC decision. Type={Type}, CorrelationId={CorrelationId}",
                decision.Type, decision.CorrelationId);
        }
    }

    private async Task HandleCreateAlertAsync(NocDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Alert == null)
        {
            _logger.LogWarning("HandleCreate decision has null Alert. CorrelationId={CorrelationId}",
                decision.CorrelationId);
            return;
        }

        // Re-check alert is still CREATE before handling
        var currentAlert = GetCurrentAlert(decision.Alert.Fingerprint);
        if (currentAlert?.Status != AlertStatus.CREATE)
        {
            _logger.LogDebug(
                "Skipping CREATE alert {Name} - status changed to {Status}. CorrelationId={CorrelationId}",
                decision.Alert.Name, currentAlert?.Status, decision.CorrelationId);
            return;
        }

        // Check if alert should be suppressed
        if (_suppressionCache.ShouldSuppress(currentAlert))
        {
            _metrics.IncrementNocSuppressed();
            _logger.LogInformation(
                "NOC Decision: Suppressed CREATE alert {Name} (Priority={Priority}, Fingerprint={Fingerprint}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                currentAlert.Name, currentAlert.Priority, currentAlert.Fingerprint, decision.CorrelationId, currentAlert.ExecutionId);
            return;
        }

        // Check if alert should be sent to NOC
        if (!currentAlert.SendToNoc)
        {
            _logger.LogDebug(
                "NOC Decision: Skipping HTTP POST for CREATE alert {Name} (send_to_noc=false). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);

            // Mark as sent to prevent re-processing, even though we didn't send to NOC
            _suppressionCache.MarkAsSent(currentAlert);
            return;
        }

        _logger.LogInformation(
            "NOC Decision: Sending HTTP POST for CREATE alert {Name} (Priority={Priority}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            currentAlert.Name, currentAlert.Priority, decision.CorrelationId, currentAlert.ExecutionId);

        // TODO: Implement actual HTTP POST to NOC service
        // For now, simulate HTTP POST delay
        await Task.Delay(100, cancellationToken); // Simulate 100ms network latency

        // Mark alert as sent after successful HTTP POST
        _suppressionCache.MarkAsSent(currentAlert);
        _metrics.IncrementNocSent();

        _logger.LogInformation(
            "NOC Decision: HTTP POST completed for CREATE alert {Name}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);
    }

    private async Task HandleCancelAlertsAsync(NocDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Alerts == null || !decision.Alerts.Any())
        {
            _logger.LogWarning("HandleCancels decision has no alerts. CorrelationId={CorrelationId}",
                decision.CorrelationId);
            return;
        }

        // Re-check which alerts are still CANCEL
        var stillCancels = decision.Alerts
            .Select(a => GetCurrentAlert(a.Fingerprint))
            .Where(a => a?.Status == AlertStatus.CANCEL)
            .ToList();

        if (!stillCancels.Any())
        {
            _logger.LogDebug(
                "Skipping CANCEL alerts - all status changed. CorrelationId={CorrelationId}",
                decision.CorrelationId);
            return;
        }

        // Separate alerts by send_to_noc flag
        var alertsToSend = stillCancels.Where(a => a != null && a.SendToNoc).ToList();
        var alertsToSkip = stillCancels.Where(a => a != null && !a.SendToNoc).ToList();

        // Log skipped alerts
        foreach (var alert in alertsToSkip)
        {
            _logger.LogDebug(
                "NOC Decision: Skipping HTTP POST for CANCEL alert {Name} (send_to_noc=false). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                alert!.Name, decision.CorrelationId, alert.ExecutionId);
        }

        // Send alerts to NOC if any
        if (alertsToSend.Any())
        {
            _logger.LogInformation(
                "NOC Decision: Sending HTTP POST for {Count} CANCEL alerts. CorrelationId={CorrelationId}",
                alertsToSend.Count, decision.CorrelationId);

            foreach (var alert in alertsToSend)
            {
                _logger.LogDebug(
                    "  - Would POST CANCEL: {Name} (Priority={Priority}) ExecutionId={ExecutionId}",
                    alert!.Name, alert.Priority, alert.ExecutionId);
            }

            // Simulate HTTP POST delay (replace with real HTTP call later)
            await Task.Delay(50, cancellationToken); // Simulate 50ms network latency

            // Track sent metrics for each alert
            foreach (var _ in alertsToSend)
            {
                _metrics.IncrementNocSent();
            }

            _logger.LogInformation(
                "NOC Decision: HTTP POST completed for {Count} CANCEL alerts. CorrelationId={CorrelationId}",
                alertsToSend.Count, decision.CorrelationId);
        }

        // Remove ALL CANCEL alerts from vector (both sent and skipped)
        // Mark successfully sent CANCELs to prevent re-sending when Prometheus sends more resolved alerts
        foreach (var alert in stillCancels)
        {
            if (alert != null)
            {
                var removed = _alertsVector.RemoveAlert(alert.Fingerprint);
                if (removed)
                {
                    _logger.LogDebug(
                        "Removed CANCEL alert from vector after successful NOC POST: {Name} (Priority={Priority}, Fingerprint={Fingerprint}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                        alert.Name, alert.Priority, alert.Fingerprint, decision.CorrelationId, alert.ExecutionId);
                }
            }
        }
    }

    private async Task HandleUnknownAlertAsync(NocDecision decision, CancellationToken cancellationToken)
    {
        if (decision.Alert == null)
        {
            _logger.LogWarning("HandleUnknown decision has null Alert. CorrelationId={CorrelationId}",
                decision.CorrelationId);
            return;
        }

        // Re-check alert is still UNKNOWN before handling
        var currentAlert = GetCurrentAlert(decision.Alert.Fingerprint);
        if (currentAlert?.Status != AlertStatus.UNKNOWN)
        {
            _logger.LogDebug(
                "Skipping UNKNOWN alert {Name} - status changed to {Status}. CorrelationId={CorrelationId}",
                decision.Alert.Name, currentAlert?.Status, decision.CorrelationId);
            return;
        }

        // Check if alert should be suppressed
        if (_suppressionCache.ShouldSuppress(currentAlert))
        {
            _metrics.IncrementNocSuppressed();
            _logger.LogInformation(
                "NOC Decision: Suppressed UNKNOWN alert {Name} (Priority={Priority}, Fingerprint={Fingerprint}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                currentAlert.Name, currentAlert.Priority, currentAlert.Fingerprint, decision.CorrelationId, currentAlert.ExecutionId);
            return;
        }

        // Check if alert should be sent to NOC
        if (!currentAlert.SendToNoc)
        {
            _logger.LogDebug(
                "NOC Decision: Skipping HTTP POST for UNKNOWN alert {Name} (send_to_noc=false). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);

            // Remove from vector since we're not sending to NOC
            _alertsVector.RemoveAlert(currentAlert.Fingerprint);
            return;
        }

        _logger.LogInformation(
            "NOC Decision: Sending HTTP POST for UNKNOWN alert {Name} (Priority={Priority}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            currentAlert.Name, currentAlert.Priority, decision.CorrelationId, currentAlert.ExecutionId);

        // TODO: Implement actual HTTP POST to NOC service
        // For now, simulate HTTP POST delay
        await Task.Delay(100, cancellationToken); // Simulate 100ms network latency

        // Mark alert as sent for suppression tracking
        _suppressionCache.MarkAsSent(currentAlert);
        _metrics.IncrementNocSent();

        // Remove from alerts vector after successful NOC POST (like CANCEL behavior)
        var removed = _alertsVector.RemoveAlert(currentAlert.Fingerprint);
        if (removed)
        {
            _logger.LogDebug(
                "Removed UNKNOWN alert from vector after successful NOC POST: {Name} (Priority={Priority}, Fingerprint={Fingerprint}). CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                currentAlert.Name, currentAlert.Priority, currentAlert.Fingerprint, decision.CorrelationId, currentAlert.ExecutionId);
        }

        _logger.LogInformation(
            "NOC Decision: HTTP POST completed for UNKNOWN alert {Name}. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            currentAlert.Name, decision.CorrelationId, currentAlert.ExecutionId);
    }

    private AlertDto? GetCurrentAlert(string fingerprint)
    {
        var snapshot = _alertsVector.GetSnapshot();
        return snapshot.FirstOrDefault(a => a.Fingerprint == fingerprint);
    }

    private void CleanupRecentlyEnqueued()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_duplicateWindowSeconds);
        var toRemove = _recentlyEnqueued
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _recentlyEnqueued.TryRemove(key, out _);
        }

        if (toRemove.Any())
        {
            _logger.LogDebug("Cleaned up {Count} old enqueue tracking entries", toRemove.Count);
        }
    }
}

