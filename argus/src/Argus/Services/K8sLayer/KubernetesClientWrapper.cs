using Argus.Configuration;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Argus.Services.K8sLayer;

/// <summary>
/// Wrapper for Kubernetes API client with retry logic and circuit breaker integration
/// </summary>
public class KubernetesClientWrapper : IKubernetesClientWrapper
{
    private readonly ILogger<KubernetesClientWrapper> _logger;
    private readonly IK8sCircuitBreaker _circuitBreaker;
    private readonly K8sLayerConfiguration _options;
    private readonly IKubernetes _client;

    public KubernetesClientWrapper(
        ILogger<KubernetesClientWrapper> logger,
        IK8sCircuitBreaker circuitBreaker,
        IOptions<ArgusConfiguration> options)
    {
        _logger = logger;
        _circuitBreaker = circuitBreaker;
        _options = options.Value.K8sLayer;

        // Initialize K8s client
        var config = _options.Kubernetes.UseInClusterConfig
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();

        _client = new Kubernetes(config);
    }

    /// <summary>
    /// Get pods matching the label selector in the configured namespace
    /// </summary>
    /// <param name="labelSelector">Kubernetes label selector</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task<V1PodList?> GetPodsAsync(
        string labelSelector,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        // Check circuit breaker first
        if (!_circuitBreaker.IsAllowed)
        {
            _logger.LogWarning(
                "Circuit breaker is open, skipping K8s API call. CorrelationId={CorrelationId}, LabelSelector={LabelSelector}",
                correlationId, labelSelector);
            return null;
        }

        var retryOptions = _options.Retry;
        Exception? lastException = null;

        for (int attempt = 0; attempt <= retryOptions.MaxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    var delayMs = retryOptions.DelayMilliseconds[Math.Min(attempt - 1, retryOptions.DelayMilliseconds.Length - 1)];
                    _logger.LogDebug(
                        "Retry attempt {Attempt}/{MaxRetries}, waiting {Delay}ms. CorrelationId={CorrelationId}, LabelSelector={LabelSelector}",
                        attempt, retryOptions.MaxRetries, delayMs, correlationId, labelSelector);
                    await Task.Delay(delayMs, cancellationToken);
                }

                var pods = await _client.CoreV1.ListNamespacedPodAsync(
                    namespaceParameter: _options.Kubernetes.Namespace,
                    labelSelector: labelSelector,
                    timeoutSeconds: _options.Kubernetes.ApiTimeoutSeconds,
                    cancellationToken: cancellationToken);

                _circuitBreaker.RecordSuccess();

                _logger.LogDebug(
                    "Successfully retrieved {Count} pods. CorrelationId={CorrelationId}, LabelSelector={LabelSelector}",
                    pods.Items.Count, correlationId, labelSelector);

                return pods;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogWarning(
                    ex,
                    "Failed to get pods, attempt {Attempt}/{MaxRetries}. CorrelationId={CorrelationId}, LabelSelector={LabelSelector}",
                    attempt + 1, retryOptions.MaxRetries + 1, correlationId, labelSelector);
            }
        }

        // All retries failed
        _circuitBreaker.RecordFailure();
        _logger.LogError(
            lastException,
            "All {MaxRetries} retries exhausted. CorrelationId={CorrelationId}, LabelSelector={LabelSelector}",
            retryOptions.MaxRetries + 1, correlationId, labelSelector);

        return null;
    }
}

/// <summary>
/// Interface for Kubernetes client wrapper
/// </summary>
public interface IKubernetesClientWrapper
{
    /// <summary>
    /// Get pods matching the label selector in the configured namespace
    /// </summary>
    /// <param name="labelSelector">Kubernetes label selector</param>
    /// <param name="correlationId">Correlation ID for tracing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<V1PodList?> GetPodsAsync(string labelSelector, string correlationId, CancellationToken cancellationToken = default);
}

