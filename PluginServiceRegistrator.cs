using CustomCoverArt.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace CustomCoverArt;

/// <summary>
/// Registers the plugin's services into Jellyfin's DI container. Jellyfin
/// discovers this type at startup — this is the correct replacement for the
/// old (non-functional) Plugin.RegisterServices approach.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // IHttpContextAccessor is already provided by the host; TryAdd keeps this safe.
        serviceCollection.AddHttpContextAccessor();

        // Stateless / shared singletons.
        serviceCollection.AddSingleton<ILoggingService, LoggingService>();
        serviceCollection.AddSingleton<ILocalizationService, LocalizationService>();
        serviceCollection.AddSingleton<IRateLimitingService, RateLimitingService>();
        serviceCollection.AddSingleton<IRetryService, RetryService>();
        serviceCollection.AddSingleton<IStartupValidationService, StartupValidationService>();

        // Per-request scoped services.
        serviceCollection.AddScoped<IImageProcessingService, ImageProcessingService>();
        serviceCollection.AddScoped<ICoverArtService, CoverArtService>();
        serviceCollection.AddScoped<ILibraryDetectionService, LibraryDetectionService>();
        serviceCollection.AddScoped<IMediaItemService, MediaItemService>();
        serviceCollection.AddScoped<IUserContextService, UserContextService>();

        // NOTE: API controllers are discovered automatically from the plugin
        // assembly by Jellyfin's MVC pipeline — they must NOT be registered here.
    }
}
