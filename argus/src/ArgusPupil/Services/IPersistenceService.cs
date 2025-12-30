using ArgusPupil.Models;

namespace ArgusPupil.Services;

/// <summary>
/// Service for persisting and loading recovery data
/// </summary>
public interface IPersistenceService
{
    /// <summary>
    /// Save recovery data to file
    /// </summary>
    /// <param name="data">Recovery data to save</param>
    /// <returns>True if saved successfully</returns>
    Task<bool> SaveRecoveryDataAsync(RecoveryData data);

    /// <summary>
    /// Load recovery data from file if it exists
    /// </summary>
    /// <returns>Recovery data or null if no file exists</returns>
    Task<RecoveryData?> LoadRecoveryDataAsync();

    /// <summary>
    /// Delete the recovery file after successful processing
    /// </summary>
    /// <returns>True if deleted successfully</returns>
    Task<bool> DeleteRecoveryDataAsync();

    /// <summary>
    /// Check if recovery data file exists
    /// </summary>
    bool HasRecoveryData();
}

