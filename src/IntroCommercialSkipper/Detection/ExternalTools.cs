using Jellyfin.Plugin.IntroCommercialSkipper.Configuration;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// Resolves the ffmpeg executable the detectors shell out to.
/// </summary>
public static class ExternalTools
{
    /// <summary>
    /// Resolves the ffmpeg executable to invoke, preferring an explicit path from this plugin's
    /// own configuration, then the ffmpeg path Jellyfin itself is already configured to use (which
    /// is correct far more often than a bare "ffmpeg" PATH lookup - Jellyfin's bundled ffmpeg is
    /// frequently not on PATH at all, e.g. at /usr/lib/jellyfin-ffmpeg/ffmpeg on the official Docker
    /// image), and only falling back to a bare PATH lookup if neither is available.
    /// </summary>
    /// <param name="config">Current plugin configuration.</param>
    /// <param name="mediaEncoder">Jellyfin's own media encoder service, if available.</param>
    /// <returns>The path or command name to launch ffmpeg with.</returns>
    public static string ResolveFfmpeg(PluginConfiguration config, IMediaEncoder? mediaEncoder)
    {
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath))
        {
            return config.FfmpegPath;
        }

        return string.IsNullOrWhiteSpace(mediaEncoder?.EncoderPath) ? "ffmpeg" : mediaEncoder.EncoderPath;
    }
}
