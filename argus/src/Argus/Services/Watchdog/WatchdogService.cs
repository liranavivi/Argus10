using Argus.Configuration;
using Argus.Models;
using Argus.Services.AlertsVector;
using Argus.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.Watchdog;

/// <summary>
/// Service to manage watchdog monitoring and generate watchdog alerts
/// </summary>
public interface IWatchdogService : IDisposable
{
    /// <summary>
    /// Record a watchdog heartbeat
    /// </summary>
    void RecordHeartbeat();

    /// <summary>
    /// Generate watchdog alert based on current state.
    /// Always generates an alert (even if healthy) to update the alerts vector.
    /// </summary>
    AlertDto GenerateAlert();

    /// <summary>
    /// Check if grace period is active
    /// </summary>
    bool IsGracePeriodActive { get; }

    /// <summary>
    /// Get watchdog state
    /// </summary>
    WatchdogState GetState();
}

public class WatchdogService : IWatchdogService
{
    private readonly ILogger<WatchdogService> _logger;
    private readonly WatchdogConfiguration _config;
    private readonly IAlertsVectorService _alertsVector;
    private readonly object _lock = new();

    private DateTime? _lastHeartbeat;
    private DateTime _startTime;
    private Timer? _expirationTimer;
    private bool _isExpired = false;

    public WatchdogService(
        ILogger<WatchdogService> logger,
        IOptions<WatchdogConfiguration> config,
        IAlertsVectorService alertsVector)
    {
        _logger = logger;
        _config = config.Value;
        _alertsVector = alertsVector;
        _startTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Generate a unique execution ID for tracking an alert through its lifecycle.
    /// Format: exec-{8-char-guid}
    /// </summary>
    private static string GenerateExecutionId() =>
        $"exec-{Guid.NewGuid().ToString("N")[..8]}";

    /// <inheritdoc />
    public void RecordHeartbeat()
    {
        lock (_lock)
        {
            _lastHeartbeat = DateTime.UtcNow;
            _isExpired = false;

            // Reset the expiration timer (cancel old, start new)
            _expirationTimer?.Dispose();

            // Only start timer if grace period is over
            if (!IsGracePeriodActive)
            {
                var timeoutMs = _config.TimeoutSeconds * 1000;
                _expirationTimer = new Timer(OnWatchdogExpired, null, timeoutMs, Timeout.Infinite);

                _logger.LogDebug("Watchdog heartbeat recorded. Timer reset to {Timeout}s", _config.TimeoutSeconds);
            }
            else
            {
                _logger.LogDebug("Watchdog heartbeat recorded (grace period active)");
            }

            // Update alerts vector with IGNORE status (watchdog is healthy)
            // Generate execution ID for this heartbeat event
            var executionId = GenerateExecutionId();
            var alert = GenerateAlert(executionId);
            _alertsVector.UpdateAlert(alert);
        }
    }

    private void OnWatchdogExpired(object? state)
    {
        lock (_lock)
        {
            _isExpired = true;

            // Generate execution ID for watchdog expiration event
            var executionId = GenerateExecutionId();

            _logger.LogWarning("Watchdog expired: No heartbeat received for {Timeout}s. ExecutionId={ExecutionId}",
                _config.TimeoutSeconds, executionId);

            // Update alerts vector with CREATE status (watchdog expired)
            var alert = GenerateAlert(executionId);
            _alertsVector.UpdateAlert(alert);
        }
    }

    /// <inheritdoc />
    public AlertDto GenerateAlert() => GenerateAlert(null);

    /// <summary>
    /// Generate watchdog alert with optional execution ID
    /// </summary>
    private AlertDto GenerateAlert(string? executionId)
    {
        var state = GetState();

        var alert = new AlertDto
        {
            Priority = -1,
            Name = "WatchdogExpired",
            Fingerprint = "watchdog",
            Source = "watchdog",
            Payload = _config.Payload,
            SendToNoc = _config.SendToNoc,
            SuppressWindow = ParseSuppressWindow(_config.SuppressWindow),
            Timestamp = DateTime.UtcNow,
            ExecutionId = executionId ?? string.Empty
        };

        if (state.Status == WatchdogStatus.Healthy)
        {
            alert.Status = AlertStatus.IGNORE;
            alert.Summary = "Watchdog is healthy";
            alert.Description = state.StatusReason;
        }
        else if (state.Status == WatchdogStatus.Initializing)
        {
            alert.Status = AlertStatus.IGNORE;
            alert.Summary = "Watchdog initializing";
            alert.Description = state.StatusReason;
        }
        else // Missing
        {
            alert.Status = AlertStatus.CREATE;
            alert.Summary = "Watchdog expired";
            alert.Description = state.StatusReason;
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
    
    /// <summary>
    /// Get the effective grace period in seconds based on crash recovery mode
    /// </summary>
    private int EffectiveGracePeriodSeconds => _alertsVector.IsCrashRecovery
        ? _config.CrashRecoveryGracePeriodSeconds
        : _config.NormalGracePeriodSeconds;

    /// <inheritdoc />
    public bool IsGracePeriodActive
    {
        get
        {
            lock (_lock)
            {
                var elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
                return elapsed < EffectiveGracePeriodSeconds;
            }
        }
    }
    
    /// <inheritdoc />
    public WatchdogState GetState()
    {
        lock (_lock)
        {
            var status = WatchdogStatus.Initializing;
            var reason = "Startup grace period active";

            if (!IsGracePeriodActive)
            {
                if (_isExpired)
                {
                    status = WatchdogStatus.Missing;
                    var secondsSinceLast = _lastHeartbeat.HasValue
                        ? (int)(DateTime.UtcNow - _lastHeartbeat.Value).TotalSeconds
                        : -1;
                    reason = _lastHeartbeat.HasValue
                        ? $"Watchdog not received for {secondsSinceLast}s (timeout: {_config.TimeoutSeconds}s)"
                        : "No watchdog ever received";
                }
                else if (_lastHeartbeat.HasValue)
                {
                    var secondsSinceLast = (DateTime.UtcNow - _lastHeartbeat.Value).TotalSeconds;
                    status = WatchdogStatus.Healthy;
                    reason = $"Watchdog received {(int)secondsSinceLast}s ago";
                }
                else
                {
                    status = WatchdogStatus.Missing;
                    reason = "No watchdog ever received";
                }
            }

            return new WatchdogState
            {
                Status = status,
                StatusReason = reason,
                LastReceivedAt = _lastHeartbeat,
                GracePeriodActive = IsGracePeriodActive
            };
        }
    }

    public void Dispose()
    {
        _expirationTimer?.Dispose();
    }
}

