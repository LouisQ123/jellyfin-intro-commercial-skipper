using Jellyfin.Plugin.IntroCommercialSkipper.Data;
using Jellyfin.Plugin.IntroCommercialSkipper.Detection;
using Jellyfin.Plugin.IntroCommercialSkipper.MediaSegments;
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
        serviceCollection.AddSingleton(sp => new SegmentDatabase(
            Plugin.Instance!.DataFolderPath,
            sp.GetRequiredService<ILogger<SegmentDatabase>>()));

        serviceCollection.AddSingleton<SegmentDetectionService>();

        serviceCollection.AddSingleton<IMediaSegmentProvider, SkipperMediaSegmentProvider>();
    }
}
