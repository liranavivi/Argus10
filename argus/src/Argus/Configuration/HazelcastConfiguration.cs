using Hazelcast;
using Hazelcast.Networking;
using Microsoft.Extensions.Options;

namespace Argus.Configuration;

/// <summary>
/// Extension methods for configuring Hazelcast client
/// </summary>
public static class HazelcastConfiguration
{
    /// <summary>
    /// Adds Hazelcast client with lazy initialization and automatic reconnection
    /// </summary>
    public static IServiceCollection AddHazelcastClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind configuration
        services.Configure<HazelcastSettings>(
            configuration.GetSection($"{ArgusConfiguration.SectionName}:Hazelcast"));

        // Register the Hazelcast client manager for lifecycle management
        services.AddSingleton<IHazelcastClientManager, HazelcastClientManager>();

        return services;
    }
}

/// <summary>
/// Interface for managing Hazelcast client lifecycle with reconnection support
/// </summary>
public interface IHazelcastClientManager
{
    /// <summary>
    /// Gets the current Hazelcast client, creating one if necessary
    /// </summary>
    Task<IHazelcastClient?> GetClientAsync();

    /// <summary>
    /// Forces recreation of the client (used when client is in unrecoverable state)
    /// </summary>
    Task RecreateClientAsync();

    /// <summary>
    /// Current connection state
    /// </summary>
    HazelcastConnectionState ConnectionState { get; }

    /// <summary>
    /// Event raised when connection state changes
    /// </summary>
    event EventHandler<HazelcastConnectionState>? ConnectionStateChanged;
}

/// <summary>
/// Hazelcast connection states for monitoring
/// </summary>
public enum HazelcastConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    ShuttingDown
}

/// <summary>
/// Manages Hazelcast client lifecycle with automatic reconnection and state tracking
/// </summary>
public class HazelcastClientManager : IHazelcastClientManager, IAsyncDisposable
{
    private readonly ILogger<HazelcastClientManager> _logger;
    private readonly HazelcastSettings _settings;
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    private IHazelcastClient? _client;
    private HazelcastConnectionState _connectionState = HazelcastConnectionState.Disconnected;
    private DateTime? _disconnectedAt;
    private bool _isDisposed;

    public event EventHandler<HazelcastConnectionState>? ConnectionStateChanged;

    public HazelcastConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (_connectionState != value)
            {
                var oldState = _connectionState;
                _connectionState = value;
                _logger.LogInformation(
                    "Hazelcast connection state changed: {OldState} -> {NewState}",
                    oldState, value);
                ConnectionStateChanged?.Invoke(this, value);
            }
        }
    }

    public HazelcastClientManager(
        ILogger<HazelcastClientManager> logger,
        IOptions<HazelcastSettings> settings)
    {
        _logger = logger;
        _settings = settings.Value;
    }

    public async Task<IHazelcastClient?> GetClientAsync()
    {
        if (_isDisposed) return null;

        // Fast path: client exists and is connected
        if (_client != null && ConnectionState == HazelcastConnectionState.Connected)
        {
            return _client;
        }

        await _clientLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_client != null && ConnectionState == HazelcastConnectionState.Connected)
            {
                return _client;
            }

            // Check if we need to recreate a dead client
            if (_client != null && ConnectionState == HazelcastConnectionState.Disconnected)
            {
                var offlineDuration = _disconnectedAt.HasValue
                    ? DateTime.UtcNow - _disconnectedAt.Value
                    : TimeSpan.Zero;

                // If disconnected for too long, recreate client
                if (offlineDuration.TotalMilliseconds > _settings.ClientRecreateThresholdMs)
                {
                    _logger.LogWarning(
                        "Hazelcast client offline for {Duration}ms, exceeds threshold of {Threshold}ms. Recreating client.",
                        offlineDuration.TotalMilliseconds,
                        _settings.ClientRecreateThresholdMs);
                    await DisposeClientAsync();
                }
            }

            // Create new client if needed
            if (_client == null)
            {
                _client = await CreateClientAsync();
            }

            return _client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    public async Task RecreateClientAsync()
    {
        await _clientLock.WaitAsync();
        try
        {
            _logger.LogInformation("Forcing Hazelcast client recreation");
            await DisposeClientAsync();
            _client = await CreateClientAsync();
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task<IHazelcastClient?> CreateClientAsync()
    {
        try
        {
            ConnectionState = HazelcastConnectionState.Connecting;

            var options = new HazelcastOptionsBuilder()
                .With(o =>
                {
                    o.ClusterName = _settings.ClusterName;
                    foreach (var address in _settings.Addresses)
                    {
                        o.Networking.Addresses.Add(address);
                    }

                    // CRITICAL: Enable automatic reconnection
                    o.Networking.ReconnectMode = ReconnectMode.ReconnectAsync;

                    // Connection retry configuration
                    o.Networking.ConnectionRetry.InitialBackoffMilliseconds = _settings.ConnectionRetry.InitialBackoffMillis;
                    o.Networking.ConnectionRetry.MaxBackoffMilliseconds = _settings.ConnectionRetry.MaxBackoffMillis;
                    o.Networking.ConnectionRetry.Multiplier = _settings.ConnectionRetry.Multiplier;
                    o.Networking.ConnectionRetry.ClusterConnectionTimeoutMilliseconds = _settings.ConnectionRetry.ClusterConnectTimeoutMillis;
                    o.Networking.ConnectionRetry.Jitter = _settings.ConnectionRetry.JitterRatio;
                })
                .Build();

            _logger.LogInformation(
                "Connecting to Hazelcast cluster: {ClusterName} at {Addresses} (ReconnectMode=ReconnectAsync, Timeout={TimeoutMs}ms)",
                _settings.ClusterName,
                string.Join(", ", _settings.Addresses),
                _settings.ConnectionRetry.ClusterConnectTimeoutMillis);

            // Use a cancellation token to enforce connection timeout
            // StartNewClientAsync with ReconnectAsync doesn't respect ClusterConnectionTimeoutMilliseconds for initial connection
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(_settings.ConnectionRetry.ClusterConnectTimeoutMillis));
            var client = await HazelcastClientFactory.StartNewClientAsync(options, cts.Token);

            // Subscribe to client state changes for logging
            await client.SubscribeAsync(events => events
                .StateChanged((sender, args) => OnClientStateChanged(args.State)));

            ConnectionState = HazelcastConnectionState.Connected;
            _disconnectedAt = null;

            _logger.LogInformation("Successfully connected to Hazelcast cluster");
            return client;
        }
        catch (OperationCanceledException)
        {
            ConnectionState = HazelcastConnectionState.Disconnected;
            _disconnectedAt = DateTime.UtcNow;
            _logger.LogWarning(
                "Hazelcast connection timed out after {TimeoutMs}ms - continuing without L2 cache",
                _settings.ConnectionRetry.ClusterConnectTimeoutMillis);
            return null;
        }
        catch (Exception ex)
        {
            ConnectionState = HazelcastConnectionState.Disconnected;
            _disconnectedAt = DateTime.UtcNow;
            _logger.LogError(ex, "Failed to connect to Hazelcast cluster");
            return null;
        }
    }

    private void OnClientStateChanged(ClientState state)
    {
        _logger.LogInformation("Hazelcast client state: {State}", state);

        switch (state)
        {
            case ClientState.Connected:
                ConnectionState = HazelcastConnectionState.Connected;
                _disconnectedAt = null;
                break;
            case ClientState.Disconnected:
                ConnectionState = HazelcastConnectionState.Disconnected;
                _disconnectedAt ??= DateTime.UtcNow;
                break;
            case ClientState.Started:
                ConnectionState = HazelcastConnectionState.Connecting;
                break;
            case ClientState.ShuttingDown:
            case ClientState.Shutdown:
                ConnectionState = HazelcastConnectionState.ShuttingDown;
                break;
            default:
                // ClusterChanged or other states - treat as reconnecting
                if (ConnectionState == HazelcastConnectionState.Disconnected)
                {
                    ConnectionState = HazelcastConnectionState.Reconnecting;
                }
                break;
        }
    }

    private async Task DisposeClientAsync()
    {
        if (_client != null)
        {
            try
            {
                await _client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing Hazelcast client");
            }
            _client = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        ConnectionState = HazelcastConnectionState.ShuttingDown;
        await DisposeClientAsync();
        _clientLock.Dispose();
    }
}

/// <summary>
/// Configuration settings for Hazelcast connection
/// </summary>
public class HazelcastSettings
{
    /// <summary>
    /// Hazelcast cluster name (must match server config)
    /// </summary>
    public string ClusterName { get; set; } = "argus-cluster";

    /// <summary>
    /// List of Hazelcast member addresses
    /// </summary>
    public List<string> Addresses { get; set; } = new() { "127.0.0.1:5701" };

    /// <summary>
    /// Name of the map used to store alerts vector
    /// </summary>
    public string AlertsMapName { get; set; } = "argus-alerts-vector";

    /// <summary>
    /// Batch window in milliseconds for L1 to L2 writes
    /// </summary>
    public int BatchWindowMs { get; set; } = 100;

    /// <summary>
    /// Maximum retry attempts for write operations
    /// </summary>
    public int MaxWriteRetries { get; set; } = 3;

    /// <summary>
    /// Initial delay between write retries in milliseconds
    /// </summary>
    public int WriteRetryDelayMs { get; set; } = 50;

    /// <summary>
    /// Connection retry configuration
    /// </summary>
    public ConnectionRetrySettings ConnectionRetry { get; set; } = new();

    /// <summary>
    /// Circuit breaker configuration for L2 persistence
    /// </summary>
    public L2CircuitBreakerSettings CircuitBreaker { get; set; } = new();

    /// <summary>
    /// Time in milliseconds after which a disconnected client should be recreated.
    /// Default: 60000 (1 minute)
    /// </summary>
    public int ClientRecreateThresholdMs { get; set; } = 60000;
}

/// <summary>
/// Connection retry settings for Hazelcast client
/// </summary>
public class ConnectionRetrySettings
{
    public int InitialBackoffMillis { get; set; } = 1000;
    public int MaxBackoffMillis { get; set; } = 30000;
    public double Multiplier { get; set; } = 2.0;
    /// <summary>
    /// Cluster connection timeout in milliseconds.
    /// Set to 10 seconds to fail fast during startup if Hazelcast is unavailable.
    /// The client will reconnect automatically in background with ReconnectAsync mode.
    /// </summary>
    public int ClusterConnectTimeoutMillis { get; set; } = 10000;
    public double JitterRatio { get; set; } = 0.2;
}

/// <summary>
/// Circuit breaker settings for L2 (Hazelcast) persistence
/// </summary>
public class L2CircuitBreakerSettings
{
    /// <summary>
    /// Number of consecutive failures before opening the circuit
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Duration in seconds the circuit stays open before allowing a probe
    /// </summary>
    public int OpenDurationSeconds { get; set; } = 30;

    /// <summary>
    /// Number of successful operations needed to close the circuit from half-open state
    /// </summary>
    public int SuccessThreshold { get; set; } = 1;

    /// <summary>
    /// Minimum interval between log messages when circuit is open (prevents log flooding)
    /// </summary>
    public int SuppressedLogIntervalSeconds { get; set; } = 10;
}

