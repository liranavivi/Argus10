using Argus.Configuration;
using Argus.Models;
using Argus.Services.Metrics;
using Argus.Utilities;
using Microsoft.Extensions.Options;

namespace Argus.Services.K8sLayer;

/// <summary>
/// K8s Layer service that orchestrates health checks for Prometheus and KSM pods
/// </summary>
public class K8sLayerService : IK8sLayerService
{
    private readonly ILogger<K8sLayerService> _logger;
    private readonly IPodHealthChecker _podHealthChecker;
    private readonly IRestartTracker _restartTracker;
    private readonly IK8sCircuitBreaker _circuitBreaker;
    private readonly IArgusMetrics _metrics;
    private readonly K8sLayerConfiguration _config;

    private K8sLayerState? _previousState;
    private readonly object _lock = new();

    public K8sLayerService(
        ILogger<K8sLayerService> logger,
        IPodHealthChecker podHealthChecker,
        IRestartTracker restartTracker,
        IK8sCircuitBreaker circuitBreaker,
        IArgusMetrics metrics,
        IOptions<K8sLayerConfiguration> config)
    {
        _logger = logger;
        _podHealthChecker = podHealthChecker;
        _restartTracker = restartTracker;
        _circuitBreaker = circuitBreaker;
        _metrics = metrics;
        _config = config.Value;
    }

    /// <summary>
    /// Get current K8s Layer state by checking both Prometheus and KSM pods
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing this request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<K8sLayerState> GetStateAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting K8s Layer health check. CorrelationId={CorrelationId}", correlationId);

        // Update circuit breaker state metric
        _metrics.SetCircuitBreakerState((int)_circuitBreaker.State);

        // Check both pods in parallel
        var prometheusTask = _podHealthChecker.CheckPrometheusHealthAsync(correlationId, cancellationToken);
        var ksmTask = _podHealthChecker.CheckKsmHealthAsync(correlationId, cancellationToken);

        await Task.WhenAll(prometheusTask, ksmTask);

        var state = new K8sLayerState
        {
            CorrelationId = correlationId,
            Prometheus = await prometheusTask,
            Ksm = await ksmTask,
            RestartTrackingGracePeriodActive = _restartTracker.IsGracePeriodActive,
            Timestamp = DateTime.UtcNow
        };

        // Calculate combined status
        state.CalculateCombinedStatus();

        // Check if state changed
        bool stateChanged = HasStateChanged(state);
        if (stateChanged)
        {
            _logger.LogInformation(
                "K8s Layer state changed: Status={Status}, Reason={Reason}. CorrelationId={CorrelationId}",
                state.Status, state.StatusReason, correlationId);
        }

        // Update previous state
        lock (_lock)
        {
            _previousState = state;
        }

        _logger.LogDebug(
            "K8s Layer health check complete: Prometheus={PrometheusStatus}, KSM={KsmStatus}, Combined={Status}. CorrelationId={CorrelationId}",
            state.Prometheus.Status, state.Ksm.Status, state.Status, correlationId);

        return state;
    }

    /// <summary>
    /// Check if current state differs from previous state
    /// </summary>
    public bool HasStateChanged(K8sLayerState currentState)
    {
        lock (_lock)
        {
            if (_previousState == null) return true;

            return _previousState.Status != currentState.Status ||
                   _previousState.Prometheus.Status != currentState.Prometheus.Status ||
                   _previousState.Ksm.Status != currentState.Ksm.Status;
        }
    }

    /// <summary>
    /// Get the previous K8s Layer state (for comparison)
    /// </summary>
    public K8sLayerState? GetPreviousState()
    {
        lock (_lock)
        {
            return _previousState;
        }
    }

    /// <summary>
    /// Get current circuit breaker state
    /// </summary>
    public CircuitState GetCircuitBreakerState() => _circuitBreaker.State;

    /// <summary>
    /// Generate AlertDto objects for current K8s layer state.
    /// Always generates alerts (even if healthy) to update the alerts vector.
    /// </summary>
    /// <param name="state">Current K8s layer state</param>
    /// <param name="executionId">Optional execution ID to assign to all alerts in this polling cycle</param>
    public List<AlertDto> GenerateAlerts(K8sLayerState state, string? executionId = null)
    {
        var alerts = new List<AlertDto>();

        // Prometheus pod alert (Priority -3)
        alerts.Add(GeneratePodAlert(
            state.Prometheus,
            _config.PrometheusPod,
            priority: -3,
            name: "PrometheusDown",
            fingerprint: "k8s-layer-prometheus",
            timestamp: state.Timestamp,
            executionId: executionId));

        // KSM pod alert (Priority -2)
        alerts.Add(GeneratePodAlert(
            state.Ksm,
            _config.KsmPod,
            priority: -2,
            name: "KSMDown",
            fingerprint: "k8s-layer-ksm",
            timestamp: state.Timestamp,
            executionId: executionId));

        return alerts;
    }

    /// <summary>
    /// Generate an AlertDto for a pod based on its health state
    /// </summary>
    private AlertDto GeneratePodAlert(
        PodHealthState podState,
        PodMonitorConfiguration config,
        int priority,
        string name,
        string fingerprint,
        DateTime timestamp,
        string? executionId)
    {
        var alert = new AlertDto
        {
            Priority = priority,
            Name = name,
            Fingerprint = fingerprint,
            Source = "K8sLayer",
            Timestamp = timestamp,
            ExecutionId = executionId ?? string.Empty
        };

        if (podState.Status == PodStatus.Healthy)
        {
            alert.Status = AlertStatus.IGNORE;
            alert.Summary = $"{name.Replace("Down", "")} pod is healthy";
            alert.Description = $"Pod '{podState.PodName}' is running normally";
            // No NOC behavior needed for IGNORE
        }
        else if (podState.Status == PodStatus.Unknown)
        {
            var unknownConfig = config.UnknownNocBehavior;
            alert.Status = AlertStatus.UNKNOWN;
            alert.Summary = $"{name.Replace("Down", "")} pod status unknown";
            alert.Description = podState.StatusReason;
            alert.Payload = unknownConfig.Payload;
            alert.SendToNoc = unknownConfig.SendToNoc;
            alert.SuppressWindow = ParseSuppressWindow(unknownConfig.SuppressWindow);
        }
        else // Down, Unstable
        {
            var createConfig = config.CreateNocBehavior;
            alert.Status = AlertStatus.CREATE;
            alert.Summary = $"{name.Replace("Down", "")} pod is {podState.Status}";
            alert.Description = podState.StatusReason;
            alert.Payload = createConfig.Payload;
            alert.SendToNoc = createConfig.SendToNoc;
            alert.SuppressWindow = ParseSuppressWindow(createConfig.SuppressWindow);
        }

        return alert;
    }

    /// <summary>
    /// Parse suppress window string to TimeSpan. Returns null if invalid.
    /// </summary>
    private static TimeSpan? ParseSuppressWindow(string? suppressWindow)
    {
        if (string.IsNullOrWhiteSpace(suppressWindow))
            return null;

        if (TimeSpanParser.TryParseToTimeSpan(suppressWindow, out var timeSpan))
            return timeSpan;

        return null;
    }
}

/// <summary>
/// Interface for K8s Layer service
/// </summary>
public interface IK8sLayerService
{
    /// <summary>
    /// Get current K8s Layer state by checking both Prometheus and KSM pods
    /// </summary>
    /// <param name="correlationId">Correlation ID for tracing this request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<K8sLayerState> GetStateAsync(string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if current state differs from previous state
    /// </summary>
    bool HasStateChanged(K8sLayerState currentState);

    /// <summary>
    /// Get the previous K8s Layer state (for comparison)
    /// </summary>
    K8sLayerState? GetPreviousState();

    /// <summary>
    /// Get current circuit breaker state
    /// </summary>
    CircuitState GetCircuitBreakerState();

    /// <summary>
    /// Generate AlertDto objects for current K8s layer state.
    /// Always generates alerts (even if healthy) to update the alerts vector.
    /// </summary>
    /// <param name="state">Current K8s layer state</param>
    /// <param name="executionId">Optional execution ID to assign to all alerts in this polling cycle</param>
    List<AlertDto> GenerateAlerts(K8sLayerState state, string? executionId = null);
}

