using Argus.Models;

namespace Argus.Services.Coordinator;

/// <summary>
/// ArgusCoordinator interface - Central coordinator for Argus monitoring.
/// Responsible for:
/// - K8s Layer polling (via K8sLayerService)
/// - Watchdog monitoring
/// - Alert filtering by platform="argus" label
/// - Real-time alerts vector management
/// - Correlation ID tracking
/// </summary>
public interface IArgusCoordinator
{
    /// <summary>
    /// Receive alerts from Prometheus (Alertmanager-compatible endpoint).
    /// Filters alerts by platform="argus" label.
    /// </summary>
    /// <param name="alerts">Alerts from Prometheus</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    void ReceiveAlerts(IEnumerable<Alert> alerts, string correlationId);

    /// <summary>
    /// Get the unified Argus state including statistics and watchdog.
    /// </summary>
    /// <returns>Unified Argus state</returns>
    ArgusState GetState();

    /// <summary>
    /// Get current watchdog state
    /// </summary>
    /// <returns>Current watchdog state</returns>
    WatchdogState GetWatchdogState();
}

