using System.Text.Json;
using Argus.Configuration;
using Argus.Models;
using Argus.Services.Metrics;
using Hazelcast;
using Microsoft.Extensions.Options;

namespace Argus.Services.AlertsVector;

/// <summary>
/// Hazelcast-based implementation of L2 persistence for alerts vector.
/// Provides reliable storage with retry logic, circuit breaker, and graceful degradation.
/// </summary>
public class AlertsPersistenceService : IAlertsPersistenceService
{
    private readonly IHazelcastClientManager _clientManager;
    private readonly IL2CircuitBreaker _circuitBreaker;
    private readonly ILogger<AlertsPersistenceService> _logger;
    private readonly IArgusMetrics _metrics;
    private readonly HazelcastSettings _settings;
    private readonly JsonSerializerOptions _jsonOptions;

    private bool _isAvailable = true;
    private DateTime? _lastSyncTimestamp;
    private readonly object _stateLock = new();

    public AlertsPersistenceService(
        IHazelcastClientManager clientManager,
        IL2CircuitBreaker circuitBreaker,
        ILogger<AlertsPersistenceService> logger,
        IArgusMetrics metrics,
        IOptions<HazelcastSettings> settings)
    {
        _clientManager = clientManager;
        _circuitBreaker = circuitBreaker;
        _logger = logger;
        _metrics = metrics;
        _settings = settings.Value;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        // Subscribe to connection state changes
        _clientManager.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void OnConnectionStateChanged(object? sender, HazelcastConnectionState state)
    {
        if (state == HazelcastConnectionState.Connected)
        {
            MarkAvailable();
            _circuitBreaker.Reset();
        }
        else if (state == HazelcastConnectionState.Disconnected)
        {
            MarkUnavailable();
        }
    }

    public bool IsAvailable
    {
        get { lock (_stateLock) return _isAvailable; }
        private set { lock (_stateLock) _isAvailable = value; }
    }

    public DateTime? LastSyncTimestamp
    {
        get { lock (_stateLock) return _lastSyncTimestamp; }
        private set { lock (_stateLock) _lastSyncTimestamp = value; }
    }

    public async Task<Dictionary<string, AlertDto>> LoadAllAsync()
    {
        var result = new Dictionary<string, AlertDto>();

        // Check circuit breaker - but always allow LoadAll during startup
        // LoadAll is typically called once at startup, so we don't apply circuit breaker here

        try
        {
            _logger.LogInformation("Loading alerts vector from Hazelcast (L2)...");

            var client = await GetClientAsync();
            if (client == null)
            {
                _logger.LogWarning("Failed to obtain Hazelcast client for LoadAll");
                _circuitBreaker.RecordFailure();
                return result;
            }

            var map = await client.GetMapAsync<string, string>(_settings.AlertsMapName);
            var entries = await map.GetEntriesAsync();

            foreach (var entry in entries)
            {
                try
                {
                    var alert = JsonSerializer.Deserialize<AlertDto>(entry.Value, _jsonOptions);
                    if (alert != null && !string.IsNullOrEmpty(alert.Fingerprint))
                    {
                        result[alert.Fingerprint] = alert;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to deserialize alert from L2. Key={Key}", entry.Key);
                }
            }

            _logger.LogInformation(
                "Loaded {Count} alerts from Hazelcast (L2)", result.Count);

            MarkAvailable();
            _circuitBreaker.RecordSuccess();
            return result;
        }
        catch (Hazelcast.Exceptions.ClientOfflineException)
        {
            _logger.LogWarning("Failed to load alerts from Hazelcast (L2): client is offline");
            MarkUnavailable();
            _circuitBreaker.RecordFailure();
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load alerts from Hazelcast (L2)");
            MarkUnavailable();
            _circuitBreaker.RecordFailure();
            return result;
        }
    }

    public async Task<bool> SaveBatchAsync(Dictionary<string, AlertDto> alerts)
    {
        if (alerts.Count == 0) return true;

        // Check circuit breaker - if open, skip the operation
        if (!_circuitBreaker.IsAllowed)
        {
            if (_circuitBreaker.ShouldLog)
            {
                _logger.LogDebug(
                    "L2 circuit breaker is open. Skipping SaveBatch of {Count} alerts. " +
                    "Data will be persisted when connection is restored.",
                    alerts.Count);
            }
            return false;
        }

        for (int attempt = 1; attempt <= _settings.MaxWriteRetries; attempt++)
        {
            try
            {
                var client = await GetClientAsync();
                if (client == null)
                {
                    if (_circuitBreaker.ShouldLog)
                    {
                        _logger.LogWarning("Failed to obtain Hazelcast client for SaveBatch");
                    }
                    _circuitBreaker.RecordFailure();
                    return false;
                }

                var map = await client.GetMapAsync<string, string>(_settings.AlertsMapName);

                // Serialize alerts and save
                foreach (var kvp in alerts)
                {
                    var json = JsonSerializer.Serialize(kvp.Value, _jsonOptions);
                    await map.SetAsync(kvp.Key, json);
                }

                LastSyncTimestamp = DateTime.UtcNow;
                MarkAvailable();
                _metrics.RecordL2WriteSuccess();
                _circuitBreaker.RecordSuccess();

                _logger.LogDebug(
                    "Saved {Count} alerts to Hazelcast (L2)", alerts.Count);
                return true;
            }
            catch (Hazelcast.Exceptions.ClientOfflineException)
            {
                if (_circuitBreaker.ShouldLog)
                {
                    _logger.LogWarning(
                        "L2 write attempt {Attempt}/{MaxRetries} failed: Hazelcast client is offline",
                        attempt, _settings.MaxWriteRetries);
                }
                _circuitBreaker.RecordFailure();

                if (attempt < _settings.MaxWriteRetries)
                {
                    await Task.Delay(_settings.WriteRetryDelayMs * attempt);
                }
            }
            catch (Exception ex)
            {
                if (_circuitBreaker.ShouldLog)
                {
                    _logger.LogWarning(ex,
                        "L2 write attempt {Attempt}/{MaxRetries} failed",
                        attempt, _settings.MaxWriteRetries);
                }

                if (attempt < _settings.MaxWriteRetries)
                {
                    await Task.Delay(_settings.WriteRetryDelayMs * attempt);
                }
            }
        }

        if (_circuitBreaker.ShouldLog)
        {
            _logger.LogError(
                "L2 write failed after {MaxRetries} retries. {Count} alerts not persisted",
                _settings.MaxWriteRetries, alerts.Count);
        }

        _metrics.RecordL2WriteFailure();
        MarkUnavailable();
        _circuitBreaker.RecordFailure();
        return false;
    }

    public async Task<bool> RemoveBatchAsync(IEnumerable<string> fingerprints)
    {
        var fingerprintList = fingerprints.ToList();
        if (fingerprintList.Count == 0) return true;

        // Check circuit breaker - if open, skip the operation
        if (!_circuitBreaker.IsAllowed)
        {
            if (_circuitBreaker.ShouldLog)
            {
                _logger.LogDebug(
                    "L2 circuit breaker is open. Skipping RemoveBatch of {Count} fingerprints.",
                    fingerprintList.Count);
            }
            return false;
        }

        for (int attempt = 1; attempt <= _settings.MaxWriteRetries; attempt++)
        {
            try
            {
                var client = await GetClientAsync();
                if (client == null)
                {
                    if (_circuitBreaker.ShouldLog)
                    {
                        _logger.LogWarning("Failed to obtain Hazelcast client for RemoveBatch");
                    }
                    _circuitBreaker.RecordFailure();
                    return false;
                }

                var map = await client.GetMapAsync<string, string>(_settings.AlertsMapName);

                foreach (var fingerprint in fingerprintList)
                {
                    await map.RemoveAsync(fingerprint);
                }

                LastSyncTimestamp = DateTime.UtcNow;
                MarkAvailable();
                _circuitBreaker.RecordSuccess();

                _logger.LogDebug(
                    "Removed {Count} alerts from Hazelcast (L2)", fingerprintList.Count);
                return true;
            }
            catch (Hazelcast.Exceptions.ClientOfflineException)
            {
                if (_circuitBreaker.ShouldLog)
                {
                    _logger.LogWarning(
                        "L2 remove attempt {Attempt}/{MaxRetries} failed: Hazelcast client is offline",
                        attempt, _settings.MaxWriteRetries);
                }
                _circuitBreaker.RecordFailure();

                if (attempt < _settings.MaxWriteRetries)
                {
                    await Task.Delay(_settings.WriteRetryDelayMs * attempt);
                }
            }
            catch (Exception ex)
            {
                if (_circuitBreaker.ShouldLog)
                {
                    _logger.LogWarning(ex,
                        "L2 remove attempt {Attempt}/{MaxRetries} failed",
                        attempt, _settings.MaxWriteRetries);
                }

                if (attempt < _settings.MaxWriteRetries)
                {
                    await Task.Delay(_settings.WriteRetryDelayMs * attempt);
                }
            }
        }

        if (_circuitBreaker.ShouldLog)
        {
            _logger.LogError(
                "L2 remove failed after {MaxRetries} retries",
                _settings.MaxWriteRetries);
        }

        _metrics.RecordL2WriteFailure();
        MarkUnavailable();
        _circuitBreaker.RecordFailure();
        return false;
    }

    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var client = await GetClientAsync();
            if (client == null) return false;

            var testMap = await client.GetMapAsync<string, string>("health-check");
            var healthy = testMap != null;

            if (healthy)
            {
                MarkAvailable();
                _circuitBreaker.RecordSuccess();
            }
            return healthy;
        }
        catch (Hazelcast.Exceptions.ClientOfflineException)
        {
            if (_circuitBreaker.ShouldLog)
            {
                _logger.LogWarning("Hazelcast health check failed: client is offline");
            }
            MarkUnavailable();
            _circuitBreaker.RecordFailure();
            return false;
        }
        catch (Exception ex)
        {
            if (_circuitBreaker.ShouldLog)
            {
                _logger.LogWarning(ex, "Hazelcast health check failed");
            }
            MarkUnavailable();
            _circuitBreaker.RecordFailure();
            return false;
        }
    }

    private async Task<IHazelcastClient?> GetClientAsync()
    {
        try
        {
            return await _clientManager.GetClientAsync();
        }
        catch (Hazelcast.Exceptions.ClientOfflineException)
        {
            if (_circuitBreaker.ShouldLog)
            {
                _logger.LogWarning("Failed to obtain Hazelcast client: client is offline");
            }
            return null;
        }
        catch (Exception ex)
        {
            if (_circuitBreaker.ShouldLog)
            {
                _logger.LogError(ex, "Failed to obtain Hazelcast client");
            }
            return null;
        }
    }

    private void MarkAvailable()
    {
        IsAvailable = true;
        _metrics.SetL2Available(true);
    }

    private void MarkUnavailable()
    {
        IsAvailable = false;
        _metrics.SetL2Available(false);
    }
}
