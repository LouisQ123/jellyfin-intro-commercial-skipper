using System.Globalization;
using Jellyfin.Plugin.IntroCommercialSkipper.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.IntroCommercialSkipper;

/// <summary>
/// Plugin entry point. Jellyfin discovers this via reflection (it implements
/// <see cref="IPlugin"/> through <see cref="BasePlugin{TConfigurationType}"/>) and instantiates it
/// once at startup.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server application paths.</param>
    /// <param name="xmlSerializer">XML serializer used by the base class to persist configuration.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the loaded plugin instance, set by the single constructor call Jellyfin makes at
    /// startup. Used by other components in this assembly (for example the media segment
    /// provider) that need the current configuration but are not themselves resolved through the
    /// plugin's own DI registration.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Intro & Commercial Skipper";

    /// <inheritdoc />
    public override Guid Id { get; } = new("b9b6a0d6-7f1b-4c9a-9b3e-5a9f6a7c2e11");

    /// <inheritdoc />
    public override string Description =>
        "Detects recurring intros/outros (via cross-episode audio fingerprinting) and commercial " +
        "breaks (via Comskip EDL import or a built-in black-frame/silence heuristic), and exposes " +
        "them to clients as skippable media segments.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "IntroCommercialSkipper",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            },
        };
    }
}
