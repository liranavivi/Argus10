using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using ArgusPupil.Configuration;
using ArgusPupil.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ArgusPupil.Services;

/// <summary>
/// Background service that hosts the HTTP/HTTPS listener for pupil messages
/// </summary>
public class PupilListenerService : BackgroundService
{
    private readonly ILogger<PupilListenerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly ArgusPupilOptions _options;
    private WebApplication? _app;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public PupilListenerService(
        ILogger<PupilListenerService> logger,
        IServiceProvider serviceProvider,
        IOptions<ArgusPupilOptions> options)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var builder = WebApplication.CreateSlimBuilder();

            // Configure Kestrel
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                serverOptions.Listen(IPAddress.Any, _options.Listener.Port, listenOptions =>
                {
                    if (_options.Listener.UseHttps)
                    {
                        ConfigureHttps(listenOptions);
                    }
                });
            });

            // Disable logging noise from Kestrel
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            _app = builder.Build();

            // Map the pupil endpoint
            _app.MapPost(_options.Listener.EndpointPath, async (HttpContext context) =>
            {
                return await HandleRequestAsync(context);
            });

            // Health check endpoint
            _app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

            var scheme = _options.Listener.UseHttps ? "https" : "http";
            _logger.LogInformation(
                "ArgusPupil listener starting on {Scheme}://0.0.0.0:{Port}{Path}",
                scheme, _options.Listener.Port, _options.Listener.EndpointPath);

            await _app.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("ArgusPupil listener shutting down");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ArgusPupil listener failed");
            throw;
        }
    }

    private void ConfigureHttps(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions)
    {
        if (string.IsNullOrEmpty(_options.Listener.CertificatePath))
        {
            throw new InvalidOperationException("Certificate path is required for HTTPS");
        }

        X509Certificate2 cert;
        if (!string.IsNullOrEmpty(_options.Listener.CertificatePassword))
        {
            cert = X509CertificateLoader.LoadPkcs12FromFile(
                _options.Listener.CertificatePath,
                _options.Listener.CertificatePassword);
        }
        else
        {
            cert = X509CertificateLoader.LoadCertificateFromFile(_options.Listener.CertificatePath);
        }

        listenOptions.UseHttps(cert);
        _logger.LogInformation("HTTPS configured with certificate: {Subject}", cert.Subject);
    }

    private async Task<IResult> HandleRequestAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
            ?? $"pupil-{Guid.NewGuid():N}";

        try
        {
            // Validate API key if configured
            if (!string.IsNullOrEmpty(_options.Listener.ApiKey))
            {
                var apiKey = context.Request.Headers["X-API-Key"].FirstOrDefault();
                if (apiKey != _options.Listener.ApiKey)
                {
                    _logger.LogWarning("Invalid API key. CorrelationId={CorrelationId}", correlationId);
                    return Results.Json(PupilResponse.Error(correlationId, "Invalid API key"), statusCode: 401);
                }
            }

            // Parse request body
            var request = await context.Request.ReadFromJsonAsync<PupilRequest>(JsonOptions);
            if (request == null)
            {
                return Results.Json(PupilResponse.Error(correlationId, "Invalid request body"), statusCode: 400);
            }

            // Ensure correlation ID is set
            if (string.IsNullOrEmpty(request.CorrelationId))
            {
                request.CorrelationId = correlationId;
            }

            // Process the message
            var handler = _serviceProvider.GetRequiredService<IMessageHandlerService>();
            var result = await handler.ProcessAsync(request, context.RequestAborted);

            context.Response.Headers["X-Correlation-ID"] = request.CorrelationId;

            if (result.Success)
            {
                var message = result.ShouldShutdown ? "Shutdown initiated" : "Message accepted";
                return Results.Json(PupilResponse.Success(request.CorrelationId, message));
            }

            return Results.Json(
                PupilResponse.Error(request.CorrelationId, result.ErrorMessage ?? "Processing failed"),
                statusCode: 500);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in request. CorrelationId={CorrelationId}", correlationId);
            return Results.Json(PupilResponse.Error(correlationId, "Invalid JSON format"), statusCode: 400);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling request. CorrelationId={CorrelationId}", correlationId);
            return Results.Json(PupilResponse.Error(correlationId, "Internal server error"), statusCode: 500);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ArgusPupil listener stopping");

        if (_app != null)
        {
            await _app.StopAsync(cancellationToken);
        }

        await base.StopAsync(cancellationToken);
    }
}

