using System.Net.Http.Json;
using System.Text.Json;
using ArgusPupil.Configuration;
using ArgusPupil.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusPupil.Services;

/// <summary>
/// HTTP client service for sending messages to NOC.
/// If all retries fail, saves recovery data and triggers application shutdown.
/// </summary>
public class NocClientService : INocClientService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NocClientService> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IPersistenceService _persistence;
    private readonly NocClientOptions _options;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public NocClientService(
        HttpClient httpClient,
        ILogger<NocClientService> logger,
        IHostApplicationLifetime lifetime,
        IPersistenceService persistence,
        IOptions<ArgusPupilOptions> options)
    {
        _httpClient = httpClient;
        _logger = logger;
        _lifetime = lifetime;
        _persistence = persistence;
        _options = options.Value.NocClient;
    }

    /// <inheritdoc />
    public Task<NocSendResult> SendAsync(NocDetails nocDetails, string correlationId, CancellationToken cancellationToken = default)
    {
        return SendAsync(nocDetails, "pupil", correlationId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<NocSendResult> SendAsync(NocDetails nocDetails, string source, string correlationId, CancellationToken cancellationToken = default)
    {
        if (!nocDetails.SendToNoc)
        {
            _logger.LogDebug(
                "Skipping NOC send (SendToNoc=false). CorrelationId={CorrelationId}",
                correlationId);
            return NocSendResult.Succeeded();
        }

        var retryCount = 0;
        var delayMs = _options.RetryDelayMs;
        Exception? lastException = null;
        int? lastStatusCode = null;

        while (retryCount <= _options.MaxRetries)
        {
            try
            {
                if (retryCount > 0)
                {
                    _logger.LogWarning(
                        "Retrying NOC send (attempt {Attempt}/{MaxRetries}). CorrelationId={CorrelationId}",
                        retryCount + 1, _options.MaxRetries + 1, correlationId);
                    await Task.Delay(delayMs, cancellationToken);
                    delayMs = (int)(delayMs * _options.RetryMultiplier);
                }

                var payload = new
                {
                    nocDetails.Priority,
                    nocDetails.Name,
                    nocDetails.Summary,
                    nocDetails.Description,
                    nocDetails.Payload,
                    Source = string.IsNullOrEmpty(nocDetails.Source) ? source : nocDetails.Source,
                    nocDetails.SuppressWindow,
                    CorrelationId = correlationId,
                    Timestamp = DateTime.UtcNow
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
                request.Headers.Add("X-Correlation-ID", correlationId);
                request.Headers.Add("X-Source", source);
                request.Content = JsonContent.Create(payload, options: JsonOptions);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                lastStatusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "NOC message sent successfully. Name={Name}, Priority={Priority}, CorrelationId={CorrelationId}",
                        nocDetails.Name, nocDetails.Priority, correlationId);
                    return NocSendResult.Succeeded(retryCount, lastStatusCode);
                }

                _logger.LogWarning(
                    "NOC send failed with status {StatusCode}. CorrelationId={CorrelationId}",
                    response.StatusCode, correlationId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.LogError(ex,
                    "NOC send error (attempt {Attempt}/{MaxRetries}). CorrelationId={CorrelationId}",
                    retryCount + 1, _options.MaxRetries + 1, correlationId);
            }

            retryCount++;
        }

        // All retries exhausted - save recovery data and trigger shutdown
        var errorMessage = lastException?.Message ?? $"HTTP {lastStatusCode}";
        _logger.LogCritical(
            "NOC send failed after {MaxRetries} retries. Saving recovery data and initiating graceful shutdown. Error={Error}, CorrelationId={CorrelationId}",
            _options.MaxRetries + 1, errorMessage, correlationId);

        // Save recovery data to file for retry on next startup
        var recoveryData = new RecoveryData
        {
            FailedAt = DateTime.UtcNow,
            CorrelationId = correlationId,
            Source = source,
            FailureReason = errorMessage,
            NocDetails = nocDetails
        };

        var saved = await _persistence.SaveRecoveryDataAsync(recoveryData);
        if (!saved)
        {
            _logger.LogError("Failed to save recovery data. NOC message will be lost. CorrelationId={CorrelationId}", correlationId);
        }

        // Trigger graceful shutdown
        _lifetime.StopApplication();

        return NocSendResult.Failed(errorMessage, retryCount, lastStatusCode);
    }
}

