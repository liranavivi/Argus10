using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Services.K8sLayer;
using Argus.Services.Metrics;
using Argus.Services.Noc;
using Argus.Services.Watchdog;
using Microsoft.Extensions.Options;

namespace Argus.Services.Coordinator;

/// <summary>
/// ArgusCoordinator - Central coordinator for Argus monitoring.
///
/// Real-time Design - Alerts vector updated asynchronously:
///
/// Alert Sources:
/// 1. K8s Layer (polling) - Updates Prometheus/KSM alerts on configured interval
/// 2. Prometheus Alerts (push) - Updates on alert arrival (platform="argus" label only)
/// 3. Watchdog (one-shot timer) - Updates when alert arrives (CANCEL) or timer expires (CREATE)
///
/// NOC Snapshots:
/// - Taken on configured interval (default: 30 seconds)
/// - Processes alerts vector in priority order
/// - All snapshots logged at DEBUG level
/// </summary>
public class ArgusCoordinator : IArgusCoordinator, IDisposable
{
    private readonly ILogger<ArgusCoordinator> _logger;
    private readonly IK8sLayerService _k8sLayerService;
    private readonly IWatchdogService _watchdogService;
    private readonly IAlertsVectorService _alertsVector;
    private readonly INocSnapshotService _nocSnapshot;
    private readonly IArgusMetrics _metrics;
    private readonly CoordinatorConfiguration _coordinatorConfig;
    private readonly K8sLayerConfiguration _k8sConfig;
    private readonly WatchdogConfiguration _watchdogConfig;

    private Timer? _pollingTimer;
    private Timer? _snapshotTimer;
    private Timer? _gracePeriodTimer;
    private bool _disposed = false;

    // Statistics (kept for backward compatibility, also tracked in metrics)
    private DateTime? _lastAlertReceivedAt;
    private readonly object _statsLock = new();

    public ArgusCoordinator(
        ILogger<ArgusCoordinator> logger,
        IK8sLayerService k8sLayerService,
        IWatchdogService watchdogService,
        IAlertsVectorService alertsVector,
        INocSnapshotService nocSnapshot,
        IArgusMetrics metrics,
        IOptions<ArgusConfiguration> config)
    {
        _logger = logger;
        _k8sLayerService = k8sLayerService;
        _watchdogService = watchdogService;
        _alertsVector = alertsVector;
        _nocSnapshot = nocSnapshot;
        _metrics = metrics;
        _coordinatorConfig = config.Value.Coordinator;
        _k8sConfig = config.Value.K8sLayer;
        _watchdogConfig = config.Value.Watchdog;

        _logger.LogInformation(
            "ArgusCoordinator started. K8s polling: {Polling}s, Snapshot interval: {Snapshot}s, Watchdog timeout: {Timeout}s",
            _k8sConfig.PollingIntervalSeconds, _coordinatorConfig.SnapshotIntervalSeconds, _watchdogConfig.TimeoutSeconds);

        // Initialize based on startup mode
        if (_alertsVector.IsCrashRecovery)
        {
            InitializeCrashRecovery();
        }
        else
        {
            InitializeNormalStartup();
        }
    }

    /// <summary>
    /// Initialize for crash recovery mode:
    /// - Poll K8s immediately
    /// - Take crash recovery snapshot (skip CREATEs, enqueue all CANCELs)
    /// - Start watchdog with short grace period (15s)
    /// - Start normal timers
    /// </summary>
    private void InitializeCrashRecovery()
    {
        _logger.LogWarning(
            "CRASH RECOVERY MODE: Immediate K8s poll, crash recovery snapshot, watchdog grace period: {GracePeriod}s",
            _watchdogConfig.CrashRecoveryGracePeriodSeconds);

        // 1. Poll K8s immediately (synchronous for crash recovery)
        var correlationId = GeneratePollingCorrelationId();
        var executionId = GenerateExecutionId();
        _ = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("CRASH RECOVERY: Polling K8s layer immediately. CorrelationId={CorrelationId}", correlationId);
                var k8sState = await _k8sLayerService.GetStateAsync(correlationId);
                var alerts = _k8sLayerService.GenerateAlerts(k8sState, executionId);

                foreach (var alert in alerts)
                {
                    _metrics.IncrementAlertsReceived("k8s_layer");
                    _alertsVector.UpdateAlert(alert);
                }

                _logger.LogInformation(
                    "CRASH RECOVERY: K8s poll complete, {AlertCount} alerts updated. CorrelationId={CorrelationId}",
                    alerts.Count, correlationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CRASH RECOVERY: K8s poll failed. CorrelationId={CorrelationId}", correlationId);
            }

            // 2. Take crash recovery snapshot (skip CREATEs, enqueue all CANCELs)
            var snapshotCorrelationId = GenerateSnapshotCorrelationId();
            try
            {
                _nocSnapshot.TakeCrashRecoverySnapshot(snapshotCorrelationId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CRASH RECOVERY: Snapshot failed. CorrelationId={CorrelationId}", snapshotCorrelationId);
            }
        });

        // 3. Start watchdog grace period timer (15s for crash recovery)
        var gracePeriodMs = _watchdogConfig.CrashRecoveryGracePeriodSeconds * 1000;
        _gracePeriodTimer = new Timer(OnGracePeriodExpired, null, gracePeriodMs, Timeout.Infinite);

        // 4. Start K8s polling timer (normal interval)
        var pollingIntervalMs = _k8sConfig.PollingIntervalSeconds * 1000;
        _pollingTimer = new Timer(OnPollingTimerFired, null, pollingIntervalMs, pollingIntervalMs);

        // 5. Start NOC snapshot timer immediately (crash recovery starts active)
        var snapshotIntervalMs = _coordinatorConfig.SnapshotIntervalSeconds * 1000;
        _snapshotTimer = new Timer(OnSnapshotTimerFired, null, snapshotIntervalMs, snapshotIntervalMs);

        _logger.LogInformation(
            "CRASH RECOVERY: Timers started. Watchdog grace period: {GracePeriod}s, NOC snapshots active immediately",
            _watchdogConfig.CrashRecoveryGracePeriodSeconds);
    }

    /// <summary>
    /// Initialize for normal startup:
    /// - Wait for grace period before starting watchdog and snapshots
    /// </summary>
    private void InitializeNormalStartup()
    {
        var gracePeriodSeconds = _watchdogConfig.NormalGracePeriodSeconds;

        // Grace period timer - fires once after grace period ends
        var gracePeriodMs = gracePeriodSeconds * 1000;
        _gracePeriodTimer = new Timer(OnGracePeriodExpired, null, gracePeriodMs, Timeout.Infinite);

        // K8s polling timer - fires on configured interval (starts immediately to populate alerts vector)
        var pollingIntervalMs = _k8sConfig.PollingIntervalSeconds * 1000;
        _pollingTimer = new Timer(OnPollingTimerFired, null, pollingIntervalMs, pollingIntervalMs);

        // NOC snapshot timer - deferred until grace period expires (no snapshots during initialization)
        // Will be started in OnGracePeriodExpired
        _snapshotTimer = null;

        _logger.LogInformation(
            "Normal startup: Grace period active ({GracePeriod}s). NOC snapshots will start after grace period ends",
            gracePeriodSeconds);
    }

    private void OnGracePeriodExpired(object? state)
    {
        _logger.LogInformation("Grace period expired. Watchdog monitoring and NOC snapshots now active");

        // Update grace period metric
        _metrics.SetGracePeriodActive(false);

        // Take first snapshot immediately
        var correlationId = GenerateSnapshotCorrelationId();
        try
        {
            _nocSnapshot.TakeSnapshot(correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First NOC snapshot failed. CorrelationId={CorrelationId}", correlationId);
        }

        // Start NOC snapshot timer for subsequent snapshots
        var snapshotIntervalMs = _coordinatorConfig.SnapshotIntervalSeconds * 1000;
        _snapshotTimer = new Timer(OnSnapshotTimerFired, null, snapshotIntervalMs, snapshotIntervalMs);
    }

    private void OnPollingTimerFired(object? state)
    {
        _ = Task.Run(async () =>
        {
            var correlationId = GeneratePollingCorrelationId();
            // Generate execution ID at the start of polling cycle - all alerts from this cycle share it
            var executionId = GenerateExecutionId();
            var startTime = DateTime.UtcNow;
            try
            {
                var k8sState = await _k8sLayerService.GetStateAsync(correlationId);
                var alerts = _k8sLayerService.GenerateAlerts(k8sState, executionId);

                foreach (var alert in alerts)
                {
                    // Track K8s layer alerts
                    _metrics.IncrementAlertsReceived("k8s_layer");
                    _alertsVector.UpdateAlert(alert);
                }

                // Record polling duration
                _metrics.RecordK8sPollDuration(DateTime.UtcNow - startTime);

                _logger.LogDebug(
                    "K8s polling complete: {AlertCount} alerts updated. CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
                    alerts.Count, correlationId, executionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "K8s polling failed. CorrelationId={CorrelationId} ExecutionId={ExecutionId}", correlationId, executionId);
            }
        });
    }

    private void OnSnapshotTimerFired(object? state)
    {
        var correlationId = GenerateSnapshotCorrelationId();
        try
        {
            _nocSnapshot.TakeSnapshot(correlationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NOC snapshot failed. CorrelationId={CorrelationId}", correlationId);
        }
    }

    #region Correlation ID and Execution ID Generation

    private static string GeneratePollingCorrelationId() =>
        $"poll-{Guid.NewGuid().ToString("N")[..8]}";

    private static string GenerateSnapshotCorrelationId() =>
        $"snapshot-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// Generate a unique execution ID for tracking an alert through its lifecycle.
    /// Format: exec-{8-char-guid}
    /// </summary>
    private static string GenerateExecutionId() =>
        $"exec-{Guid.NewGuid().ToString("N")[..8]}";

    #endregion

    #region IArgusCoordinator Implementation

    /// <inheritdoc />
    public void ReceiveAlerts(IEnumerable<Alert> alerts, string correlationId)
    {
        var alertList = alerts.ToList();

        lock (_statsLock)
        {
            _lastAlertReceivedAt = DateTime.UtcNow;
        }

        foreach (var alert in alertList)
        {
            // Track received alert (source: prometheus_push)
            _metrics.IncrementAlertsReceived("prometheus_push");

            // Check platform label - only process alerts with platform="argus"
            // Alerts without this label or with different value are completely ignored (not even logged)
            if (!alert.ShouldBeProcessed())
            {
                _metrics.IncrementAlertsFiltered();
                continue;
            }

            // platform=argus - process the alert
            ProcessAlert(alert, correlationId);
        }
    }

    private void ProcessAlert(Alert alert, string correlationId)
    {
        // Generate execution ID for this alert (tracks it through its entire lifecycle)
        var executionId = GenerateExecutionId();
        alert.ExecutionId = executionId;

        // Check if this is a watchdog alert (special handling)
        if (alert.Name.Equals(_watchdogConfig.AlertName, StringComparison.OrdinalIgnoreCase))
        {
            if (alert.IsFiring)
            {
                // Record heartbeat - this will reset the timer and update alerts vector with CANCEL
                // Note: Watchdog heartbeat generates its own execution ID when it updates the alert
                _watchdogService.RecordHeartbeat();
                _logger.LogDebug(
                    "Watchdog alert received: 1 alert updated (heartbeat recorded, timer reset). CorrelationId={CorrelationId}, ExecutionId={ExecutionId}",
                    correlationId, executionId);
            }
            return;
        }

        // Convert to AlertDto and update vector (ExecutionId is copied in ToAlertDto)
        var alertDto = alert.ToAlertDto();
        _alertsVector.UpdateAlert(alertDto);

        _logger.LogDebug(
            "Prometheus alert received: Name={Name} Priority={Priority} Status={Status} Fingerprint={Fingerprint} CorrelationId={CorrelationId} ExecutionId={ExecutionId}",
            alertDto.Name, alertDto.Priority, alertDto.Status, alertDto.Fingerprint, correlationId, executionId);
    }

    /// <inheritdoc />
    public ArgusState GetState()
    {
        var metricsSnapshot = _metrics.GetSnapshot();
        lock (_statsLock)
        {
            return new ArgusState
            {
                TotalAlertsReceived = metricsSnapshot.TotalAlertsReceived,
                TotalAlertsFiltered = metricsSnapshot.TotalAlertsFiltered,
                LastAlertReceivedAt = _lastAlertReceivedAt,
                Watchdog = _watchdogService.GetState()
            };
        }
    }

    /// <inheritdoc />
    public WatchdogState GetWatchdogState() => _watchdogService.GetState();

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;

        _pollingTimer?.Dispose();
        _snapshotTimer?.Dispose();
        _gracePeriodTimer?.Dispose();
        _watchdogService?.Dispose();

        _disposed = true;
        _logger.LogInformation("ArgusCoordinator disposed");
    }

    #endregion
}

