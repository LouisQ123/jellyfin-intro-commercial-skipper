using Jellyfin.Plugin.IntroCommercialSkipper.Configuration;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// Resolves the ffmpeg executable the detectors shell out to, honoring an explicit path from
/// configuration and otherwise falling back to PATH lookup.
/// </summary>
public static class ExternalTools
{
    /// <summary>
    /// Resolves the ffmpeg executable to invoke.
    /// </summary>
    /// <param name="config">Current plugin configuration.</param>
    /// <returns>An explicit path if configured, otherwise the bare command name to resolve via PATH.</returns>
    public static string ResolveFfmpeg(PluginConfiguration config) =>
        string.IsNullOrWhiteSpace(config.FfmpegPath) ? "ffmpeg" : config.FfmpegPath;
}
