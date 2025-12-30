using ArgusPupil.Configuration;
using ArgusPupil.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusPupil.Services;

/// <summary>
/// Watchdog timer that monitors heartbeats and triggers NOC send on expiry
/// </summary>
public class WatchdogTimerService : IWatchdogTimerService
{
    private readonly ILogger<WatchdogTimerService> _logger;
    private readonly INocClientService _nocClient;
    private readonly WatchdogOptions _options;
    private readonly object _lock = new();

    private Timer? _expirationTimer;
    private Timer? _gracePeriodTimer;
    private DateTime? _lastHeartbeat;
    private NocDetails? _lastNocDetails;
    private int _currentTimeoutSeconds;
    private bool _isExpired;
    private bool _isGracePeriodActive = true;
    private bool _isActive;
    private bool _isDisposed;

    public WatchdogTimerService(
        ILogger<WatchdogTimerService> logger,
        INocClientService nocClient,
        IOptions<ArgusPupilOptions> options)
    {
        _logger = logger;
        _nocClient = nocClient;
        _options = options.Value.Watchdog;
        _currentTimeoutSeconds = _options.DefaultTimeoutSeconds;
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_lock)
        {
            if (_options.GracePeriodSeconds > 0)
            {
                _isGracePeriodActive = true;
                _gracePeriodTimer = new Timer(
                    OnGracePeriodExpired,
                    null,
                    _options.GracePeriodSeconds * 1000,
                    Timeout.Infinite);

                _logger.LogInformation(
                    "Watchdog started with {GracePeriod}s grace period, default timeout: {Timeout}s",
                    _options.GracePeriodSeconds, _options.DefaultTimeoutSeconds);
            }
            else
            {
                _isGracePeriodActive = false;
                _logger.LogInformation(
                    "Watchdog started with no grace period, default timeout: {Timeout}s",
                    _options.DefaultTimeoutSeconds);
            }
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_lock)
        {
            _expirationTimer?.Dispose();
            _expirationTimer = null;
            _gracePeriodTimer?.Dispose();
            _gracePeriodTimer = null;
            _isActive = false;
            _logger.LogInformation("Watchdog stopped");
        }
    }

    /// <inheritdoc />
    public void ProcessHeartbeat(HeartbeatMessage message)
    {
        lock (_lock)
        {
            _lastHeartbeat = DateTime.UtcNow;
            _lastNocDetails = message.NocDetails;
            _isExpired = false;
            _isActive = true;

            // Update timeout if specified in message
            if (message.TimeoutSeconds.HasValue && message.TimeoutSeconds.Value > 0)
            {
                _currentTimeoutSeconds = message.TimeoutSeconds.Value;
            }

            // Cancel existing timer and start new one
            _expirationTimer?.Dispose();

            // Only start timer if grace period is over
            if (!_isGracePeriodActive)
            {
                var timeoutMs = _currentTimeoutSeconds * 1000;
                _expirationTimer = new Timer(OnWatchdogExpired, null, timeoutMs, Timeout.Infinite);

                _logger.LogDebug(
                    "Heartbeat received, timer reset to {Timeout}s. CorrelationId={CorrelationId}",
                    _currentTimeoutSeconds, message.CorrelationId);
            }
            else
            {
                _logger.LogDebug(
                    "Heartbeat received during grace period. CorrelationId={CorrelationId}",
                    message.CorrelationId);
            }
        }
    }

    private void OnGracePeriodExpired(object? state)
    {
        lock (_lock)
        {
            _isGracePeriodActive = false;
            _gracePeriodTimer?.Dispose();
            _gracePeriodTimer = null;

            _logger.LogInformation("Watchdog grace period expired, monitoring now active");

            // If we received heartbeats during grace period, start the timer
            if (_lastHeartbeat.HasValue)
            {
                var timeoutMs = _currentTimeoutSeconds * 1000;
                _expirationTimer = new Timer(OnWatchdogExpired, null, timeoutMs, Timeout.Infinite);
            }
        }
    }

    private void OnWatchdogExpired(object? state)
    {
        lock (_lock)
        {
            _isExpired = true;
            var correlationId = $"watchdog-{Guid.NewGuid():N}";

            _logger.LogWarning(
                "Watchdog expired: No heartbeat received for {Timeout}s. CorrelationId={CorrelationId}",
                _currentTimeoutSeconds, correlationId);

            // Send last NOC details if available
            if (_lastNocDetails != null)
            {
                // Fire and forget - NOC client will handle shutdown if it fails
                _ = Task.Run(async () =>
                {
                    await _nocClient.SendAsync(_lastNocDetails, "watchdog", correlationId);
                });
            }
        }
    }

    /// <inheritdoc />
    public WatchdogTimerState GetState()
    {
        lock (_lock)
        {
            return new WatchdogTimerState
            {
                IsActive = _isActive,
                IsExpired = _isExpired,
                IsGracePeriodActive = _isGracePeriodActive,
                LastHeartbeat = _lastHeartbeat,
                TimeoutSeconds = _currentTimeoutSeconds
            };
        }
    }

    /// <inheritdoc />
    public NocDetails? GetLastNocDetails()
    {
        lock (_lock)
        {
            return _lastNocDetails;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        lock (_lock)
        {
            _expirationTimer?.Dispose();
            _gracePeriodTimer?.Dispose();
            _isDisposed = true;
        }
    }
}

