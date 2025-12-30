using ArgusApi;
using ArgusClientApp.Services;
using ArgusClientApp.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Build the host with Argus monitoring
var builder = Host.CreateApplicationBuilder(args);

// Add Argus client wrapper with OTel integration (includes Console + OTel logging)
// CompositeKey should match K8s pod label: argus.io/composite-key
// Read CollectorEndpoint from configuration (env var: ArgusApi__CollectorEndpoint)
var collectorEndpoint = builder.Configuration["ArgusApi:CollectorEndpoint"] ?? "http://localhost:4317";
builder.Services.AddArgusClientWrapper(options =>
{
    options.CompositeKey = "argusclientapp_v1";
    options.Payload = "component=argusclientapp,type=heartbeat,severity=high";
    options.SendToNoc = true; // Enable sending alerts to NOC
    options.SuppressWindow = "5m"; // Suppress duplicate alerts for 5 minutes
    options.CollectorEndpoint = collectorEndpoint;
    options.MetricExportInterval = TimeSpan.FromSeconds(15);
    // options.TelemetrySource = "custom"; // Disable Argus alert monitoring for all metrics

    // Add custom meters for business metrics (not coupled to Argus NOC alerts)
    options.AdditionalMeters.Add("ArgusClientApp.Business");
});

// Register services
builder.Services.AddSingleton<OrderProcessingService>();

// Register background workers
builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<SimpleLoopWorker>();
builder.Services.AddHostedService<CustomMetricsWorker>();
builder.Services.AddHostedService<NonArgusWorker>(); // Example: worker not inheriting from ArgusBackgroundService

var host = builder.Build();

// Get logger and monitor for startup message
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var monitor = host.Services.GetRequiredService<IArgusMonitor>();

logger.LogInformation(
    "ArgusClientApp starting. TelemetryPrefix={Prefix}, CollectorEndpoint={Endpoint}",
    monitor.TelemetryPrefix,
    collectorEndpoint);

// Run the host
await host.RunAsync();
