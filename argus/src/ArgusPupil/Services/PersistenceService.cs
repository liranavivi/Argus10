using System.Text.Json;
using ArgusPupil.Configuration;
using ArgusPupil.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusPupil.Services;

/// <summary>
/// File-based persistence service for recovery data
/// </summary>
public class PersistenceService : IPersistenceService
{
    private readonly ILogger<PersistenceService> _logger;
    private readonly PersistenceOptions _options;
    private readonly object _fileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public PersistenceService(
        ILogger<PersistenceService> logger,
        IOptions<ArgusPupilOptions> options)
    {
        _logger = logger;
        _options = options.Value.Persistence;

        // Ensure storage directory exists
        EnsureStorageDirectory();
    }

    private void EnsureStorageDirectory()
    {
        try
        {
            if (!Directory.Exists(_options.StoragePath))
            {
                Directory.CreateDirectory(_options.StoragePath);
                _logger.LogInformation("Created storage directory: {Path}", _options.StoragePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create storage directory: {Path}", _options.StoragePath);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<bool> SaveRecoveryDataAsync(RecoveryData data)
    {
        var filePath = _options.RecoveryFilePath;

        try
        {
            var json = JsonSerializer.Serialize(data, JsonOptions);

            lock (_fileLock)
            {
                // Write to temp file first, then move for atomicity
                var tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, filePath, overwrite: true);
            }

            _logger.LogInformation(
                "Recovery data saved successfully. CorrelationId={CorrelationId}, Path={Path}",
                data.CorrelationId, filePath);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to save recovery data. CorrelationId={CorrelationId}, Path={Path}",
                data.CorrelationId, filePath);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<RecoveryData?> LoadRecoveryDataAsync()
    {
        var filePath = _options.RecoveryFilePath;

        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("No recovery file found at {Path}", filePath);
                return Task.FromResult<RecoveryData?>(null);
            }

            string json;
            lock (_fileLock)
            {
                json = File.ReadAllText(filePath);
            }

            var data = JsonSerializer.Deserialize<RecoveryData>(json, JsonOptions);

            if (data != null)
            {
                _logger.LogInformation(
                    "Recovery data loaded. FailedAt={FailedAt}, CorrelationId={CorrelationId}",
                    data.FailedAt, data.CorrelationId);
            }

            return Task.FromResult(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load recovery data from {Path}", filePath);
            return Task.FromResult<RecoveryData?>(null);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteRecoveryDataAsync()
    {
        var filePath = _options.RecoveryFilePath;

        try
        {
            lock (_fileLock)
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogInformation("Recovery file deleted: {Path}", filePath);
                }
            }

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete recovery file: {Path}", filePath);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public bool HasRecoveryData()
    {
        return File.Exists(_options.RecoveryFilePath);
    }
}

