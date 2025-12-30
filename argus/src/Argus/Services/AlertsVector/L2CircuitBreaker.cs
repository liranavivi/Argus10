using Argus.Configuration;
using Argus.Models;
using Microsoft.Extensions.Options;

namespace Argus.Services.AlertsVector;

/// <summary>
/// Circuit breaker for L2 (Hazelcast) persistence operations.
/// Prevents log flooding and unnecessary retry attempts when Hazelcast is clearly offline.
/// </summary>
public class L2CircuitBreaker : IL2CircuitBreaker
{
    private readonly ILogger<L2CircuitBreaker> _logger;
    private readonly L2CircuitBreakerSettings _settings;

    private CircuitState _state = CircuitState.Closed;
    private int _failureCount;
    private int _successCount;
    private DateTime? _openedAt;
    private DateTime? _lastLogTime;
    private int _suppressedOperationCount;
    private readonly object _lock = new();

    public L2CircuitBreaker(
        ILogger<L2CircuitBreaker> logger,
        IOptions<HazelcastSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value.CircuitBreaker;
    }

    public CircuitState State
    {
        get
        {
            lock (_lock)
            {
                // Check if we should transition from Open to HalfOpen
                if (_state == CircuitState.Open && _openedAt.HasValue)
                {
                    var elapsed = DateTime.UtcNow - _openedAt.Value;
                    if (elapsed.TotalSeconds >= _settings.OpenDurationSeconds)
                    {
                        _state = CircuitState.HalfOpen;
                        _logger.LogInformation(
                            "L2 circuit breaker transitioning from Open to HalfOpen after {Seconds}s. " +
                            "{SuppressedCount} operations were suppressed.",
                            _settings.OpenDurationSeconds,
                            _suppressedOperationCount);
                        _suppressedOperationCount = 0;
                    }
                }
                return _state;
            }
        }
    }

    public bool IsAllowed
    {
        get
        {
            var currentState = State; // This triggers the Open->HalfOpen check
            return currentState != CircuitState.Open;
        }
    }

    public bool ShouldLog
    {
        get
        {
            lock (_lock)
            {
                if (_state != CircuitState.Open)
                {
                    return true;
                }

                // When open, only log periodically to prevent flooding
                var now = DateTime.UtcNow;
                if (!_lastLogTime.HasValue ||
                    (now - _lastLogTime.Value).TotalSeconds >= _settings.SuppressedLogIntervalSeconds)
                {
                    _lastLogTime = now;
                    return true;
                }

                _suppressedOperationCount++;
                return false;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                _successCount++;
                if (_successCount >= _settings.SuccessThreshold)
                {
                    _state = CircuitState.Closed;
                    _failureCount = 0;
                    _successCount = 0;
                    _openedAt = null;
                    _lastLogTime = null;
                    _logger.LogInformation(
                        "L2 circuit breaker closed after successful recovery. Hazelcast connection restored.");
                }
            }
            else if (_state == CircuitState.Closed)
            {
                // Reset failure count on success
                _failureCount = 0;
            }
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _failureCount++;

            if (_state == CircuitState.HalfOpen)
            {
                // Any failure in HalfOpen immediately opens the circuit
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
                _successCount = 0;
                _suppressedOperationCount = 0;
                _logger.LogWarning(
                    "L2 circuit breaker opened from HalfOpen state. Will retry in {Seconds}s",
                    _settings.OpenDurationSeconds);
            }
            else if (_state == CircuitState.Closed && _failureCount >= _settings.FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
                _suppressedOperationCount = 0;
                _logger.LogWarning(
                    "L2 circuit breaker opened after {Count} consecutive failures. " +
                    "Hazelcast appears offline. Will probe in {Seconds}s. " +
                    "Subsequent failures will be suppressed to prevent log flooding.",
                    _failureCount,
                    _settings.OpenDurationSeconds);
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _failureCount = 0;
            _successCount = 0;
            _openedAt = null;
            _lastLogTime = null;
            _suppressedOperationCount = 0;
            _logger.LogInformation("L2 circuit breaker manually reset to Closed state");
        }
    }
}

/// <summary>
/// Interface for L2 circuit breaker
/// </summary>
public interface IL2CircuitBreaker
{
    /// <summary>
    /// Current circuit state
    /// </summary>
    CircuitState State { get; }

    /// <summary>
    /// Whether operations are currently allowed
    /// </summary>
    bool IsAllowed { get; }

    /// <summary>
    /// Whether logging should occur (prevents log flooding when circuit is open)
    /// </summary>
    bool ShouldLog { get; }

    /// <summary>
    /// Record a successful operation
    /// </summary>
    void RecordSuccess();

    /// <summary>
    /// Record a failed operation
    /// </summary>
    void RecordFailure();

    /// <summary>
    /// Manually reset the circuit breaker
    /// </summary>
    void Reset();
}

