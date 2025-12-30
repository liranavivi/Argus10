namespace ArgusPupil.Configuration;

/// <summary>
/// Configuration options for ArgusPupil library
/// </summary>
public class ArgusPupilOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json
    /// </summary>
    public const string SectionName = "ArgusPupil";

    /// <summary>
    /// HTTP listener configuration
    /// </summary>
    public ListenerOptions Listener { get; set; } = new();

    /// <summary>
    /// NOC client configuration
    /// </summary>
    public NocClientOptions NocClient { get; set; } = new();

    /// <summary>
    /// Watchdog timer configuration
    /// </summary>
    public WatchdogOptions Watchdog { get; set; } = new();

    /// <summary>
    /// File persistence configuration
    /// </summary>
    public PersistenceOptions Persistence { get; set; } = new();

    /// <summary>
    /// Event handler configuration
    /// </summary>
    public EventHandlerOptions EventHandler { get; set; } = new();

    /// <summary>
    /// Validates the configuration options
    /// </summary>
    public void Validate()
    {
        Listener.Validate();
        NocClient.Validate();
        Watchdog.Validate();
        Persistence.Validate();
        EventHandler.Validate();
    }
}

/// <summary>
/// HTTP/HTTPS listener configuration
/// </summary>
public class ListenerOptions
{
    /// <summary>
    /// Port to listen on. Default: 5100
    /// </summary>
    public int Port { get; set; } = 5100;

    /// <summary>
    /// Enable HTTPS with TLS. Default: false
    /// </summary>
    public bool UseHttps { get; set; } = false;

    /// <summary>
    /// Path to the X.509 certificate file (.pfx) for HTTPS
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Password for the certificate file
    /// </summary>
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// API endpoint path. Default: /pupil
    /// </summary>
    public string EndpointPath { get; set; } = "/pupil";

    /// <summary>
    /// Optional API key for authentication. If set, requests must include X-API-Key header
    /// </summary>
    public string? ApiKey { get; set; }

    public void Validate()
    {
        if (Port < 1 || Port > 65535)
            throw new ArgumentException("Port must be between 1 and 65535", nameof(Port));

        if (UseHttps && string.IsNullOrWhiteSpace(CertificatePath))
            throw new ArgumentException("CertificatePath is required when UseHttps is true", nameof(CertificatePath));
    }
}

/// <summary>
/// NOC client configuration for sending messages
/// </summary>
public class NocClientOptions
{
    /// <summary>
    /// NOC service endpoint URL. Required.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// HTTP timeout in seconds. Default: 30
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of retry attempts. Default: 3
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Initial retry delay in milliseconds. Default: 1000
    /// </summary>
    public int RetryDelayMs { get; set; } = 1000;

    /// <summary>
    /// Retry delay multiplier for exponential backoff. Default: 2.0
    /// </summary>
    public double RetryMultiplier { get; set; } = 2.0;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Endpoint))
            throw new ArgumentException("NOC Endpoint is required", nameof(Endpoint));

        if (TimeoutSeconds < 1)
            throw new ArgumentException("TimeoutSeconds must be at least 1", nameof(TimeoutSeconds));

        if (MaxRetries < 0)
            throw new ArgumentException("MaxRetries cannot be negative", nameof(MaxRetries));
    }
}

/// <summary>
/// Watchdog timer configuration
/// </summary>
public class WatchdogOptions
{
    /// <summary>
    /// Default timeout in seconds before watchdog expires. Default: 60
    /// </summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Grace period in seconds on startup before watchdog becomes active. Default: 30
    /// </summary>
    public int GracePeriodSeconds { get; set; } = 30;

    public void Validate()
    {
        if (DefaultTimeoutSeconds < 1)
            throw new ArgumentException("DefaultTimeoutSeconds must be at least 1", nameof(DefaultTimeoutSeconds));
    }
}

/// <summary>
/// File persistence configuration for KillYourself messages
/// </summary>
public class PersistenceOptions
{
    /// <summary>
    /// Directory path for storing persistence files. Default: current directory
    /// </summary>
    public string StoragePath { get; set; } = ".";

    /// <summary>
    /// Filename for the KillYourself message persistence. Default: arguspupil_recovery.json
    /// </summary>
    public string RecoveryFileName { get; set; } = "arguspupil_recovery.json";

    /// <summary>
    /// Full path to the recovery file
    /// </summary>
    public string RecoveryFilePath => Path.Combine(StoragePath, RecoveryFileName);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RecoveryFileName))
            throw new ArgumentException("RecoveryFileName is required", nameof(RecoveryFileName));
    }
}

/// <summary>
/// Event handler configuration
/// </summary>
public class EventHandlerOptions
{
    /// <summary>
    /// Maximum time in seconds to wait for event handlers to complete. Default: 30
    /// </summary>
    public int HandlerTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum number of concurrent event handlers. Default: 4
    /// </summary>
    public int MaxConcurrentHandlers { get; set; } = 4;

    public void Validate()
    {
        if (HandlerTimeoutSeconds < 1)
            throw new ArgumentException("HandlerTimeoutSeconds must be at least 1", nameof(HandlerTimeoutSeconds));

        if (MaxConcurrentHandlers < 1)
            throw new ArgumentException("MaxConcurrentHandlers must be at least 1", nameof(MaxConcurrentHandlers));
    }
}

