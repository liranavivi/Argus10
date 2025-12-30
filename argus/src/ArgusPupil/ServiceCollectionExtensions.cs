using ArgusPupil.Configuration;
using ArgusPupil.Events;
using ArgusPupil.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ArgusPupil;

/// <summary>
/// Extension methods for configuring ArgusPupil services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add ArgusPupil services to the service collection
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddArgusPupil(
        this IServiceCollection services,
        Action<ArgusPupilOptions> configure)
    {
        // Configure options
        services.Configure(configure);

        // Validate options on startup
        services.AddSingleton<IValidateOptions<ArgusPupilOptions>, ArgusPupilOptionsValidator>();

        // Register core services
        services.AddSingleton<IPersistenceService, PersistenceService>();
        services.AddSingleton<IWatchdogTimerService, WatchdogTimerService>();
        services.AddSingleton<IMessageHandlerService, MessageHandlerService>();

        // Register NOC client with HttpClient and retry policy
        services.AddHttpClient<INocClientService, NocClientService>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ArgusPupilOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.NocClient.TimeoutSeconds);
        });

        // Register hosted services
        services.AddHostedService<StartupRecoveryService>();
        services.AddHostedService<PupilListenerService>();

        return services;
    }

    /// <summary>
    /// Add ArgusPupil services with configuration from IConfiguration
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configurationSection">The configuration section</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddArgusPupil(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfigurationSection configurationSection)
    {
        services.Configure<ArgusPupilOptions>(configurationSection);

        // Validate options on startup
        services.AddSingleton<IValidateOptions<ArgusPupilOptions>, ArgusPupilOptionsValidator>();

        // Register core services
        services.AddSingleton<IPersistenceService, PersistenceService>();
        services.AddSingleton<IWatchdogTimerService, WatchdogTimerService>();
        services.AddSingleton<IMessageHandlerService, MessageHandlerService>();

        // Register NOC client with HttpClient
        services.AddHttpClient<INocClientService, NocClientService>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<ArgusPupilOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.NocClient.TimeoutSeconds);
        });

        // Register hosted services
        services.AddHostedService<StartupRecoveryService>();
        services.AddHostedService<PupilListenerService>();

        return services;
    }

    /// <summary>
    /// Add a custom event handler for pupil messages
    /// </summary>
    /// <typeparam name="THandler">The handler type</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPupilEventHandler<THandler>(this IServiceCollection services)
        where THandler : class, IPupilEventHandler
    {
        services.AddSingleton<IPupilEventHandler, THandler>();
        return services;
    }
}

/// <summary>
/// Options validator for ArgusPupilOptions
/// </summary>
internal class ArgusPupilOptionsValidator : IValidateOptions<ArgusPupilOptions>
{
    public ValidateOptionsResult Validate(string? name, ArgusPupilOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }
}

