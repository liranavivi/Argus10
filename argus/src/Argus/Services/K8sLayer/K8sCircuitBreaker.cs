using Argus.Configuration;
using Argus.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.K8sLayer;

/// <summary>
/// Circuit breaker implementation for K8s API calls.
/// Prevents overwhelming the API when it's experiencing issues.
/// </summary>
public class K8sCircuitBreaker : IK8sCircuitBreaker
{
    private readonly ILogger<K8sCircuitBreaker> _logger;
    private readonly CircuitBreakerConfiguration _options;

    private CircuitState _state = CircuitState.Closed;
    private int _failureCount;
    private int _successCount;
    private DateTime? _openedAt;
    private readonly object _lock = new();

    public K8sCircuitBreaker(
        ILogger<K8sCircuitBreaker> logger,
        IOptions<ArgusConfiguration> options)
    {
        _logger = logger;
        _options = options.Value.K8sLayer.CircuitBreaker;
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
                    if (elapsed.TotalSeconds >= _options.OpenDurationSeconds)
                    {
                        _state = CircuitState.HalfOpen;
                        _logger.LogInformation(
                            "Circuit breaker transitioning from Open to HalfOpen after {Seconds}s",
                            _options.OpenDurationSeconds);
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

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state == CircuitState.HalfOpen)
            {
                _successCount++;
                if (_successCount >= _options.SuccessThreshold)
                {
                    _state = CircuitState.Closed;
                    _failureCount = 0;
                    _successCount = 0;
                    _openedAt = null;
                    _logger.LogInformation("Circuit breaker closed after successful recovery");
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
                _logger.LogWarning(
                    "Circuit breaker opened from HalfOpen state, will retry in {Seconds}s",
                    _options.OpenDurationSeconds);
            }
            else if (_state == CircuitState.Closed && _failureCount >= _options.FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "Circuit breaker opened after {Count} consecutive failures, will retry in {Seconds}s",
                    _failureCount,
                    _options.OpenDurationSeconds);
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
            _logger.LogInformation("Circuit breaker manually reset to Closed state");
        }
    }
}

/// <summary>
/// Interface for K8s circuit breaker
/// </summary>
public interface IK8sCircuitBreaker
{
    CircuitState State { get; }
    bool IsAllowed { get; }
    void RecordSuccess();
    void RecordFailure();
    void Reset();
}

