using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace ArgusApi;

/// <summary>
/// Implementation of Argus monitoring with OTel integration.
/// </summary>
public sealed class ArgusMonitor : IArgusMonitor, IDisposable
{
    private readonly ArgusMonitoringOptions _options;
    private readonly Meter _meter;
    private readonly Counter<long> _heartbeatCounter;
    private readonly Counter<long> _exceptionCounter;
    private readonly ConcurrentDictionary<string, bool> _registeredWorkers = new();
    private bool _disposed;

    /// <inheritdoc />
    public ActivitySource ActivitySource { get; }

    /// <inheritdoc />
    public string TelemetryPrefix => _options.TelemetryPrefix;

    public ArgusMonitor(IOptions<ArgusMonitoringOptions> options)
    {
        _options = options.Value;
        _options.Validate();

        // Create meter with name: argus_api (for ArgusApi client metrics)
        _meter = new Meter(ServiceCollectionExtensions.ArgusApiMeterName);

        // Create heartbeat counter: argus_heartbeat_total
        // Use argus_ prefix for namespacing - all Argus metrics are easily filterable
        // Labels: composite_key, worker, payload, send_to_noc, suppress_window (always present)
        _heartbeatCounter = _meter.CreateCounter<long>(
            "argus_heartbeat",
            unit: "count",
            description: "Heartbeat counter for background workers");

        // Create exception counter: argus_exceptions_total
        _exceptionCounter = _meter.CreateCounter<long>(
            "argus_exceptions",
            unit: "count",
            description: "Exception counter with type and context dimensions");

        // Create ActivitySource for tracing
        ActivitySource = new ActivitySource(_options.TelemetryPrefix);
    }

    /// <inheritdoc />
    public void Heartbeat(object worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        var workerName = GetWorkerName(worker.GetType());
        RecordHeartbeat(workerName);
    }

    /// <inheritdoc />
    public void Heartbeat<T>()
    {
        var workerName = GetWorkerName(typeof(T));
        RecordHeartbeat(workerName);
    }

    /// <inheritdoc />
    public void Heartbeat(string componentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentName);
        RecordHeartbeat(componentName);
    }

    private void RecordHeartbeat(string workerName)
    {
        // Auto-register on first heartbeat
        _registeredWorkers.TryAdd(workerName, true);

        // Add composite_key, worker, payload, send_to_noc, and suppress_window as labels
        // composite_key matches K8s pod label: argus.io/composite-key
        // payload contains developer-defined description for alert enrichment
        // send_to_noc controls whether alerts are sent to NOC
        // suppress_window controls alert deduplication window (empty string = no suppression)
        var tags = new TagList
        {
            { "composite_key", _options.NormalizedCompositeKey },
            { "worker", workerName },
            { "payload", _options.Payload },
            { "send_to_noc", _options.SendToNoc.ToString().ToLowerInvariant() },
            { "suppress_window", _options.SuppressWindow }
        };

        _heartbeatCounter.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordException(object worker, Exception exception, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(exception);

        var workerName = GetWorkerName(worker.GetType());
        RecordExceptionInternal(exception, workerName, operation);
    }

    /// <inheritdoc />
    public void RecordException(Exception exception, string? component = null, string? operation = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        RecordExceptionInternal(exception, component, operation);
    }

    private void RecordExceptionInternal(Exception exception, string? workerOrComponent, string? operation)
    {
        var tags = new TagList
        {
            { "composite_key", _options.NormalizedCompositeKey },
            { "type", exception.GetType().Name },
            { "payload", _options.Payload },
            { "send_to_noc", _options.SendToNoc.ToString().ToLowerInvariant() },
            { "suppress_window", _options.SuppressWindow }
        };

        if (!string.IsNullOrEmpty(workerOrComponent))
            tags.Add("worker", workerOrComponent);

        if (!string.IsNullOrEmpty(operation))
            tags.Add("operation", operation);

        _exceptionCounter.Add(1, tags);
    }

    /// <inheritdoc />
    public Activity? StartTrace(string operationName)
    {
        return ActivitySource.StartActivity(operationName);
    }

    /// <inheritdoc />
    public Activity? StartTrace(string operationName, ActivityKind kind)
    {
        return ActivitySource.StartActivity(operationName, kind);
    }

    private static string GetWorkerName(Type type)
    {
        return type.Name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _meter.Dispose();
        ActivitySource.Dispose();
    }
}

