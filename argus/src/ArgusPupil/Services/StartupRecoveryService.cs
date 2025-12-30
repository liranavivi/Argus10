using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArgusPupil.Services;

/// <summary>
/// Service that runs on startup to check for recovery data and send to NOC
/// </summary>
public class StartupRecoveryService : IHostedService
{
    private readonly ILogger<StartupRecoveryService> _logger;
    private readonly IPersistenceService _persistence;
    private readonly INocClientService _nocClient;
    private readonly IWatchdogTimerService _watchdog;

    public StartupRecoveryService(
        ILogger<StartupRecoveryService> logger,
        IPersistenceService persistence,
        INocClientService nocClient,
        IWatchdogTimerService watchdog)
    {
        _logger = logger;
        _persistence = persistence;
        _nocClient = nocClient;
        _watchdog = watchdog;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ArgusPupil starting up...");

        // Check for recovery data from previous failed NOC send
        await ProcessRecoveryDataAsync(cancellationToken);

        // Start the watchdog timer
        _watchdog.Start();

        _logger.LogInformation("ArgusPupil startup complete");
    }

    private async Task ProcessRecoveryDataAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_persistence.HasRecoveryData())
            {
                _logger.LogDebug("No recovery data found - fresh start");
                return;
            }

            _logger.LogInformation("Recovery data detected - processing...");

            var recoveryData = await _persistence.LoadRecoveryDataAsync();
            if (recoveryData == null)
            {
                _logger.LogWarning("Failed to load recovery data - file may be corrupted");
                await _persistence.DeleteRecoveryDataAsync();
                return;
            }

            // Update recovery timestamp
            recoveryData.RecoveredAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Processing recovery from failed NOC send at {FailedAt}. Reason={Reason}, CorrelationId={CorrelationId}",
                recoveryData.FailedAt, recoveryData.FailureReason, recoveryData.CorrelationId);

            // Modify the NOC details to indicate this is a recovery message
            var nocDetails = recoveryData.NocDetails;
            nocDetails.Summary = $"[RECOVERY] {nocDetails.Summary}";
            nocDetails.Description = $"Message recovered from failed send.\nOriginal failure time: {recoveryData.FailedAt:O}\nRecovery time: {recoveryData.RecoveredAt:O}\nFailure reason: {recoveryData.FailureReason}\n\n{nocDetails.Description}";

            // Send to NOC
            var result = await _nocClient.SendAsync(
                nocDetails,
                $"recovery-{recoveryData.Source}",
                $"recovery-{recoveryData.CorrelationId}",
                cancellationToken);

            if (result.Success)
            {
                _logger.LogInformation(
                    "Recovery message sent successfully. CorrelationId={CorrelationId}",
                    recoveryData.CorrelationId);

                // Delete the recovery file
                await _persistence.DeleteRecoveryDataAsync();
            }
            else
            {
                // NOC send failed again - the NocClientService will handle saving new recovery data and shutdown
                _logger.LogError(
                    "Failed to send recovery message. Error={Error}, CorrelationId={CorrelationId}",
                    result.ErrorMessage, recoveryData.CorrelationId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing recovery data");
            // Don't delete the file on error - let it retry on next startup
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ArgusPupil shutting down...");

        // Stop the watchdog timer
        _watchdog.Stop();

        return Task.CompletedTask;
    }
}

