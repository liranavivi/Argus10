using System.Collections.Concurrent;
using Argus.Configuration;
using Argus.Models;
using Argus.Utilities;
using Microsoft.Extensions.Options;

namespace Argus.Services.Noc;

/// <summary>
/// Cache for suppressing duplicate NOC HTTP messages based on fingerprint and time window.
/// Prevents sending the same alert multiple times within a configured suppression window.
/// </summary>
public interface ISuppressionCache
{
    /// <summary>
    /// Check if an alert should be suppressed based on its fingerprint and suppression window
    /// </summary>
    /// <param name="alert">Alert to check</param>
    /// <returns>True if alert should be suppressed (already sent recently)</returns>
    bool ShouldSuppress(AlertDto alert);

    /// <summary>
    /// Mark an alert as sent with its suppression window
    /// </summary>
    /// <param name="alert">Alert that was sent</param>
    void MarkAsSent(AlertDto alert);

    /// <summary>
    /// Clean up old entries that are outside their suppression windows
    /// </summary>
    void Cleanup();
}

public class SuppressionCache : ISuppressionCache
{
    private readonly ILogger<SuppressionCache> _logger;
    private readonly NocConfiguration _config;
    private readonly ConcurrentDictionary<string, SuppressionEntry> _entries = new();

    public SuppressionCache(
        ILogger<SuppressionCache> logger,
        IOptions<NocConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
    }

    /// <inheritdoc />
    public bool ShouldSuppress(AlertDto alert)
    {
        // Check if suppression window is 0 (empty string means no suppression)
        var windowSeconds = GetSuppressionWindow(alert);
        if (windowSeconds == 0)
        {
            return false; // No suppression requested
        }

        if (!_entries.TryGetValue(alert.Fingerprint, out var entry))
        {
            return false; // Never sent, don't suppress
        }

        var age = DateTime.UtcNow - entry.LastSent;
        var shouldSuppress = age.TotalSeconds < entry.WindowSeconds;

        if (shouldSuppress)
        {
            _logger.LogDebug(
                "Suppressing alert {Name} (fingerprint={Fingerprint}). Last sent {Age:F1}s ago, window={Window}s",
                alert.Name, alert.Fingerprint, age.TotalSeconds, entry.WindowSeconds);
        }

        return shouldSuppress;
    }

    /// <inheritdoc />
    public void MarkAsSent(AlertDto alert)
    {
        var windowSeconds = GetSuppressionWindow(alert);

        // Only track if suppression is enabled (windowSeconds > 0)
        if (windowSeconds > 0)
        {
            _entries[alert.Fingerprint] = new SuppressionEntry
            {
                LastSent = DateTime.UtcNow,
                WindowSeconds = windowSeconds
            };

            _logger.LogDebug(
                "Marked alert {Name} (fingerprint={Fingerprint}) as sent with {Window}s suppression window",
                alert.Name, alert.Fingerprint, windowSeconds);
        }
        else
        {
            _logger.LogDebug(
                "Alert {Name} (fingerprint={Fingerprint}) sent with no suppression (suppress_window is empty)",
                alert.Name, alert.Fingerprint);
        }
    }

    /// <inheritdoc />
    public void Cleanup()
    {
        var now = DateTime.UtcNow;
        var removed = 0;

        foreach (var kvp in _entries)
        {
            var age = (now - kvp.Value.LastSent).TotalSeconds;
            if (age > kvp.Value.WindowSeconds)
            {
                _entries.TryRemove(kvp.Key, out _);
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogDebug(
                "Suppression cache cleanup: removed {Removed} expired entries, {Remaining} remaining",
                removed, _entries.Count);
        }
    }

    /// <summary>
    /// Get suppression window for an alert based on SuppressWindow property, annotations, or default.
    /// Returns 0 for empty string (no suppression), or Noc.DefaultWindow for missing/invalid.
    /// </summary>
    private int GetSuppressionWindow(AlertDto alert)
    {
        // 1. Try to get from SuppressWindow property (preferred - set by K8sLayer, Watchdog, or API)
        if (alert.SuppressWindow.HasValue)
        {
            var seconds = (int)alert.SuppressWindow.Value.TotalSeconds;
            _logger.LogDebug(
                "Using SuppressWindow property for {Name}: {Seconds}s",
                alert.Name, seconds);
            return seconds;
        }

        // 2. Try to get from annotations (supports formats like "120s", "4m", "8h", "3d")
        if (alert.Annotations.TryGetValue("suppress_window", out var windowStr))
        {
            // Empty string means no suppression (explicit opt-out)
            if (string.IsNullOrWhiteSpace(windowStr))
            {
                _logger.LogDebug(
                    "No suppression for {Name}: suppress_window is empty",
                    alert.Name);
                return 0;
            }

            // Try to parse the value
            if (TimeSpanParser.TryParseToSeconds(windowStr, out var seconds))
            {
                _logger.LogDebug(
                    "Using suppress_window from annotation for {Name}: {Window} ({Seconds}s)",
                    alert.Name, windowStr, seconds);
                return seconds;
            }
            else
            {
                // Invalid format - fall through to default
                _logger.LogWarning(
                    "Invalid suppress_window annotation for {Name}: '{Window}'. Using default: {Default}",
                    alert.Name, windowStr, _config.DefaultWindow);
            }
        }

        // 3. Use default for all other cases (invalid/missing suppress_window)
        var defaultSeconds = TimeSpanParser.ParseToSeconds(_config.DefaultWindow);
        _logger.LogDebug(
            "Using default suppression window for {Name}: {Default} ({Seconds}s)",
            alert.Name, _config.DefaultWindow, defaultSeconds);
        return defaultSeconds;
    }
}

/// <summary>
/// Entry in the suppression cache
/// </summary>
internal class SuppressionEntry
{
    public DateTime LastSent { get; set; }
    public int WindowSeconds { get; set; }
}

