using ArgusPupil.Models;

namespace ArgusPupil.Events;

/// <summary>
/// Base interface for pupil event handlers.
/// Implement this interface to receive notifications when messages are received.
/// Handlers are executed in the background and should not block message processing.
/// </summary>
public interface IPupilEventHandler
{
    /// <summary>
    /// Called when a heartbeat message is received.
    /// Override to implement custom logic on heartbeat (e.g., trigger work, update status).
    /// </summary>
    /// <param name="message">The heartbeat message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task OnHeartbeatReceivedAsync(HeartbeatMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a KillYourself message is received, before the application shuts down.
    /// Override to implement cleanup logic before shutdown.
    /// </summary>
    /// <param name="message">The kill message</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task OnKillYourselfReceivedAsync(KillYourselfMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a SendNocMessage command is received.
    /// Override to intercept or modify NOC messages before they are sent.
    /// </summary>
    /// <param name="message">The send NOC message command</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task OnSendNocMessageReceivedAsync(SendNocMessageCommand message, CancellationToken cancellationToken);
}

/// <summary>
/// Default no-op implementation of IPupilEventHandler.
/// Inherit from this class and override only the methods you need.
/// </summary>
public abstract class PupilEventHandlerBase : IPupilEventHandler
{
    /// <inheritdoc />
    public virtual Task OnHeartbeatReceivedAsync(HeartbeatMessage message, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task OnKillYourselfReceivedAsync(KillYourselfMessage message, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task OnSendNocMessageReceivedAsync(SendNocMessageCommand message, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

