namespace ArgusApi;

/// <summary>
/// Configuration options for Argus monitoring.
/// </summary>
public class ArgusMonitoringOptions
{
    /// <summary>
    /// Composite key for identifying the service instance.
    /// Used for K8s pod labeling correlation: argus.io/composite-key
    /// Example: "orderservice_v1" -> TelemetryPrefix becomes "argus_orderservice_v1"
    /// </summary>
    public string CompositeKey { get; set; } = string.Empty;

    /// <summary>
    /// Payload description for all metrics.
    /// Included as a label in metrics and passed to alert annotations for context.
    /// Example: "Order processing service monitoring"
    /// </summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>
    /// Whether this service should send alerts to NOC.
    /// When true, alerts will be sent to NOC when heartbeat stops.
    /// When false, alerts will be generated but not sent to NOC.
    /// Default is false.
    /// </summary>
    public bool SendToNoc { get; set; } = false;

    /// <summary>
    /// How long to suppress duplicate alert notifications.
    /// Included as a label in metrics and passed to alert annotations.
    /// Examples: "5m", "15m", "1h"
    /// Empty string (default) means no suppression.
    /// If not set or invalid, Argus will use defaults from appsettings.json.
    /// </summary>
    public string SuppressWindow { get; set; } = "";

    /// <summary>
    /// OpenTelemetry Collector endpoint (gRPC).
    /// </summary>
    public string CollectorEndpoint { get; set; } = "http://localhost:4317";

    /// <summary>
    /// Interval for exporting metrics to the collector.
    /// </summary>
    public TimeSpan MetricExportInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Whether to add console exporter for logs (in addition to OTLP).
    /// Default is true for visibility during development.
    /// </summary>
    public bool UseConsoleExporter { get; set; } = true;

    /// <summary>
    /// Additional meter names to capture. The "argus" meter is always included.
    /// Use this to export custom application metrics via OTel.
    /// Example: "MyApp.Business", "MyApp.Performance"
    /// </summary>
    public List<string> AdditionalMeters { get; set; } = new();

    /// <summary>
    /// Telemetry source identifier for all metrics from this service.
    /// Default is "argus_api" which enables Argus alert monitoring.
    /// Set to "custom" to export metrics to Prometheus/Grafana without triggering Argus alerts.
    /// This sets the telemetry_source resource attribute applied to all metrics.
    /// </summary>
    public string TelemetrySource { get; set; } = "argus_api";

    /// <summary>
    /// Gets the normalized composite key (lowercase, no spaces, no dots).
    /// </summary>
    internal string NormalizedCompositeKey => CompositeKey
        .ToLowerInvariant()
        .Replace(" ", "")
        .Replace("-", "_")
        .Replace(".", "_");

    /// <summary>
    /// Gets the telemetry prefix: argus_{compositeKey}
    /// </summary>
    internal string TelemetryPrefix => $"argus_{NormalizedCompositeKey}";

    /// <summary>
    /// Validates the options.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(CompositeKey))
            throw new ArgumentException("CompositeKey is required", nameof(CompositeKey));

        if (string.IsNullOrWhiteSpace(CollectorEndpoint))
            throw new ArgumentException("CollectorEndpoint is required", nameof(CollectorEndpoint));
    }
}

