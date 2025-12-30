using Argus.Configuration;
using Argus.Services.AlertsVector;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.K8sLayer;

/// <summary>
/// Tracks pod restart counts using a sliding window to detect restart storms.
/// </summary>
public class RestartTracker : IRestartTracker
{
    private readonly ILogger<RestartTracker> _logger;
    private readonly RestartTrackingConfiguration _options;
    private readonly IAlertsVectorService _alertsVector;
    private readonly DateTime _startupTime;
    private bool _gracePeriodEndLogged = false;

    // Key: pod identifier (e.g., "prometheus" or "ksm"), Value: sliding window of restart counts
    private readonly Dictionary<string, Queue<int>> _restartWindows = new();
    private readonly object _lock = new();

    public RestartTracker(
        ILogger<RestartTracker> logger,
        IOptions<ArgusConfiguration> options,
        IAlertsVectorService alertsVector)
    {
        _logger = logger;
        _options = options.Value.K8sLayer.RestartTracking;
        _alertsVector = alertsVector;
        _startupTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Whether the grace period after Argus startup is still active.
    /// During crash recovery, there is no grace period (immediate detection).
    /// </summary>
    public bool IsGracePeriodActive
    {
        get
        {
            // No grace period during crash recovery - immediate restart storm detection
            if (_alertsVector.IsCrashRecovery)
            {
                if (!_gracePeriodEndLogged)
                {
                    _gracePeriodEndLogged = true;
                    _logger.LogInformation(
                        "Restart tracking active immediately (crash recovery mode - no grace period)");
                }
                return false;
            }

            var elapsed = DateTime.UtcNow - _startupTime;
            var isActive = elapsed.TotalSeconds < _options.NormalGracePeriodSeconds;

            // Log once when grace period ends
            if (!isActive && !_gracePeriodEndLogged)
            {
                _gracePeriodEndLogged = true;
                _logger.LogInformation(
                    "Restart tracking grace period ended. Restart storm detection active. " +
                    "Mode=Normal, GracePeriod={GracePeriodSeconds}s",
                    _options.NormalGracePeriodSeconds);
            }

            return isActive;
        }
    }

    /// <summary>
    /// Record a restart count observation and calculate restarts within the window
    /// </summary>
    public (int RestartsInWindow, bool IsStable) RecordRestartCount(string podId, int currentRestartCount)
    {
        lock (_lock)
        {
            // Initialize window if not exists
            if (!_restartWindows.TryGetValue(podId, out var window))
            {
                window = new Queue<int>();
                _restartWindows[podId] = window;
            }

            // Add current count to window
            window.Enqueue(currentRestartCount);

            // Trim to window size
            while (window.Count > _options.WindowSize)
            {
                window.Dequeue();
            }

            // Calculate restarts in window
            int restartsInWindow = 0;
            if (window.Count >= 2)
            {
                var oldest = window.Peek();
                restartsInWindow = currentRestartCount - oldest;
            }

            // During grace period, always report as stable
            if (IsGracePeriodActive)
            {
                _logger.LogDebug(
                    "Pod {PodId}: restart count={Count}, window size={WindowSize}, " +
                    "restarts in window={RestartsInWindow} (grace period active, reporting stable)",
                    podId, currentRestartCount, window.Count, restartsInWindow);
                return (restartsInWindow, true);
            }

            // Need full window to make stability determination
            if (window.Count < _options.WindowSize)
            {
                _logger.LogDebug(
                    "Pod {PodId}: restart count={Count}, window size={WindowSize}/{Required}, " +
                    "building history (reporting stable)",
                    podId, currentRestartCount, window.Count, _options.WindowSize);
                return (restartsInWindow, true);
            }

            bool isStable = restartsInWindow < _options.RestartThreshold;
            
            _logger.LogDebug(
                "Pod {PodId}: restart count={Count}, restarts in window={RestartsInWindow}, " +
                "threshold={Threshold}, stable={IsStable}",
                podId, currentRestartCount, restartsInWindow, _options.RestartThreshold, isStable);

            return (restartsInWindow, isStable);
        }
    }

    /// <summary>
    /// Get the current restart window for a pod
    /// </summary>
    public List<int> GetRestartWindow(string podId)
    {
        lock (_lock)
        {
            if (_restartWindows.TryGetValue(podId, out var window))
            {
                return window.ToList();
            }
            return [];
        }
    }

    /// <summary>
    /// Clear tracking data for a pod (e.g., when pod is replaced)
    /// </summary>
    public void ClearPod(string podId)
    {
        lock (_lock)
        {
            _restartWindows.Remove(podId);
            _logger.LogDebug("Cleared restart tracking for pod {PodId}", podId);
        }
    }
}

/// <summary>
/// Interface for restart tracker
/// </summary>
public interface IRestartTracker
{
    bool IsGracePeriodActive { get; }
    (int RestartsInWindow, bool IsStable) RecordRestartCount(string podId, int currentRestartCount);
    List<int> GetRestartWindow(string podId);
    void ClearPod(string podId);
}

