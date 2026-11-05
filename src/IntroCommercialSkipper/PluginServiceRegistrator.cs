using Jellyfin.Plugin.IntroCommercialSkipper.Data;
using Jellyfin.Plugin.IntroCommercialSkipper.Detection;
using Jellyfin.Plugin.IntroCommercialSkipper.MediaSegments;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper;

/// <summary>
/// Registers this plugin's services with Jellyfin's dependency injection container. Jellyfin
/// instantiates this class itself via reflection (it requires a parameterless constructor) and
/// calls <see cref="RegisterServices"/> once, before the container is built - registering here is
/// required for <see cref="SkipperMediaSegmentProvider"/> to be constructor-injected with its own
/// dependencies and to be picked up by Jellyfin's <c>IMediaSegmentManager</c>, which resolves all
/// <see cref="IMediaSegmentProvider"/> implementations through this same container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Deliberately resolved from the DI container at first use (IApplicationPaths is a core
        // server service, registered well before plugins are instantiated) rather than read from
        // Plugin.Instance: IMediaSegmentManager eagerly resolves every IMediaSegmentProvider - and
        // therefore this factory - as soon as the container is built, which can happen before
        // Plugin's own constructor (which sets Plugin.Instance) has run.
        serviceCollection.AddSingleton(sp =>
        {
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var dataDirectory = Path.Combine(appPaths.PluginsPath, "IntroCommercialSkipper");
            return new SegmentDatabase(dataDirectory, sp.GetRequiredService<ILogger<SegmentDatabase>>());
        });

        serviceCollection.AddSingleton<SegmentDetectionService>();

        serviceCollection.AddSingleton<IMediaSegmentProvider, SkipperMediaSegmentProvider>();
    }
}
