using System.Threading.Channels;
using ArgusPupil.Configuration;
using ArgusPupil.Events;
using ArgusPupil.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusPupil.Services;

/// <summary>
/// Service for handling incoming messages, routing to handlers, and dispatching events
/// </summary>
public class MessageHandlerService : IMessageHandlerService
{
    private readonly ILogger<MessageHandlerService> _logger;
    private readonly IWatchdogTimerService _watchdog;
    private readonly INocClientService _nocClient;
    private readonly IPersistenceService _persistence;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IEnumerable<IPupilEventHandler> _eventHandlers;
    private readonly EventHandlerOptions _options;
    private readonly Channel<Func<CancellationToken, Task>> _eventQueue;

    public MessageHandlerService(
        ILogger<MessageHandlerService> logger,
        IWatchdogTimerService watchdog,
        INocClientService nocClient,
        IPersistenceService persistence,
        IHostApplicationLifetime lifetime,
        IEnumerable<IPupilEventHandler> eventHandlers,
        IOptions<ArgusPupilOptions> options)
    {
        _logger = logger;
        _watchdog = watchdog;
        _nocClient = nocClient;
        _persistence = persistence;
        _lifetime = lifetime;
        _eventHandlers = eventHandlers;
        _options = options.Value.EventHandler;

        // Create bounded channel for event queue
        _eventQueue = Channel.CreateBounded<Func<CancellationToken, Task>>(
            new BoundedChannelOptions(_options.MaxConcurrentHandlers * 2)
            {
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    /// <inheritdoc />
    public async Task<MessageProcessResult> ProcessAsync(PupilRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = request.ToTypedMessage();

            _logger.LogInformation(
                "Processing {MessageType} message. CorrelationId={CorrelationId}",
                message.MessageType, message.CorrelationId);

            return message switch
            {
                HeartbeatMessage hb => await ProcessHeartbeatAsync(hb, cancellationToken),
                KillYourselfMessage kill => await ProcessKillYourselfAsync(kill, cancellationToken),
                SendNocMessageCommand send => await ProcessSendNocMessageAsync(send, cancellationToken),
                _ => MessageProcessResult.Failed($"Unknown message type: {message.MessageType}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message. CorrelationId={CorrelationId}", request.CorrelationId);
            return MessageProcessResult.Failed(ex.Message);
        }
    }

    private Task<MessageProcessResult> ProcessHeartbeatAsync(HeartbeatMessage message, CancellationToken cancellationToken)
    {
        // Reset watchdog timer
        _watchdog.ProcessHeartbeat(message);

        // Dispatch event to handlers (fire-and-forget)
        DispatchEvent(async ct =>
        {
            foreach (var handler in _eventHandlers)
            {
                try
                {
                    await handler.OnHeartbeatReceivedAsync(message, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in heartbeat event handler: {Handler}", handler.GetType().Name);
                }
            }
        });

        return Task.FromResult(MessageProcessResult.Succeeded());
    }

    private async Task<MessageProcessResult> ProcessKillYourselfAsync(KillYourselfMessage message, CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            "KillYourself received. Reason={Reason}, CorrelationId={CorrelationId}",
            message.Reason, message.CorrelationId);

        // Save recovery data to file
        var recoveryData = new RecoveryData
        {
            KilledAt = DateTime.UtcNow,
            CorrelationId = message.CorrelationId,
            Reason = message.Reason,
            NocDetails = message.NocDetails
        };

        var saved = await _persistence.SaveRecoveryDataAsync(recoveryData);
        if (!saved)
        {
            _logger.LogError("Failed to save recovery data. Shutdown will proceed without recovery file.");
        }

        // Dispatch event to handlers and wait for them to complete before shutdown
        foreach (var handler in _eventHandlers)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HandlerTimeoutSeconds));
                await handler.OnKillYourselfReceivedAsync(message, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in kill event handler: {Handler}", handler.GetType().Name);
            }
        }

        // Trigger graceful shutdown
        _lifetime.StopApplication();

        return MessageProcessResult.ShutdownRequired();
    }

    private async Task<MessageProcessResult> ProcessSendNocMessageAsync(SendNocMessageCommand message, CancellationToken cancellationToken)
    {
        // Dispatch event to handlers first (fire-and-forget)
        DispatchEvent(async ct =>
        {
            foreach (var handler in _eventHandlers)
            {
                try
                {
                    await handler.OnSendNocMessageReceivedAsync(message, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in send NOC event handler: {Handler}", handler.GetType().Name);
                }
            }
        });

        // Send to NOC immediately
        var result = await _nocClient.SendAsync(message.NocDetails, "command", message.CorrelationId, cancellationToken);

        return result.Success
            ? MessageProcessResult.Succeeded()
            : MessageProcessResult.Failed(result.ErrorMessage ?? "NOC send failed");
    }

    /// <summary>
    /// Dispatch an event handler to run in the background
    /// </summary>
    private void DispatchEvent(Func<CancellationToken, Task> handler)
    {
        // Fire-and-forget with timeout
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.HandlerTimeoutSeconds));
                await handler(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Event handler timed out after {Timeout}s", _options.HandlerTimeoutSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing event handler");
            }
        });
    }
}

