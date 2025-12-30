using Argus.Models;

namespace Argus.Services.AlertsVector;

/// <summary>
/// Interface for L2 (Hazelcast) persistence layer of the alerts vector.
/// Provides async operations for loading, saving, and health checking.
/// </summary>
public interface IAlertsPersistenceService
{
    /// <summary>
    /// Load all alerts from L2 storage.
    /// Called on startup to recover state after crash.
    /// </summary>
    /// <returns>Dictionary of fingerprint -> AlertDto, or empty if no data or failure</returns>
    Task<Dictionary<string, AlertDto>> LoadAllAsync();

    /// <summary>
    /// Save a batch of alerts to L2 storage.
    /// Used by the batch writer for periodic sync.
    /// </summary>
    /// <param name="alerts">Alerts to save (keyed by fingerprint)</param>
    /// <returns>True if save succeeded, false otherwise</returns>
    Task<bool> SaveBatchAsync(Dictionary<string, AlertDto> alerts);

    /// <summary>
    /// Remove alerts from L2 storage.
    /// Called when alerts are removed from L1.
    /// </summary>
    /// <param name="fingerprints">Fingerprints of alerts to remove</param>
    /// <returns>True if removal succeeded, false otherwise</returns>
    Task<bool> RemoveBatchAsync(IEnumerable<string> fingerprints);

    /// <summary>
    /// Check if L2 storage is healthy and accessible.
    /// </summary>
    /// <returns>True if healthy, false otherwise</returns>
    Task<bool> IsHealthyAsync();

    /// <summary>
    /// Get the current state of L2 availability.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Get the timestamp of the last successful L2 sync.
    /// </summary>
    DateTime? LastSyncTimestamp { get; }
}

