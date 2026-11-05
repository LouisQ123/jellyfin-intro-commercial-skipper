using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Configuration;

/// <summary>
/// User-editable settings for the plugin, exposed via the config page and persisted by Jellyfin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether intro/outro detection (cross-episode audio
    /// fingerprint correlation) runs during the scheduled scan.
    /// </summary>
    public bool EnableIntroOutroDetection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether commercial-break detection runs during the
    /// scheduled scan. Comskip EDL files are always imported when present; this flag controls
    /// only the built-in black-frame/silence heuristic fallback.
    /// </summary>
    public bool EnableCommercialDetection { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the built-in black-frame/silence heuristic should
    /// run when no Comskip EDL file is found next to the media. Off by default: the heuristic is
    /// slow and far less reliable than Comskip, so it should be opted into deliberately.
    /// </summary>
    public bool EnableHeuristicCommercialFallback { get; set; }

    /// <summary>
    /// Gets or sets the minimum duration, in seconds, a detected intro/outro segment must span to
    /// be kept. Filters out spurious short matches.
    /// </summary>
    public int MinIntroOutroDurationSeconds { get; set; } = 8;

    /// <summary>
    /// Gets or sets the maximum duration, in seconds, of the window searched at the start and end
    /// of each episode for a recurring intro/outro.
    /// </summary>
    public int IntroOutroSearchWindowSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets the maximum fraction of mismatched fingerprint bits (0-1) allowed for two
    /// segments to still be considered the same intro/outro. Lower is stricter.
    /// </summary>
    public double FingerprintMaxBitErrorRate { get; set; } = 0.35;

    /// <summary>
    /// Gets or sets the minimum duration, in seconds, of a black+silence window to be treated as a
    /// candidate commercial-break boundary by the heuristic detector.
    /// </summary>
    public double HeuristicMinBreakSeconds { get; set; } = 0.4;

    /// <summary>
    /// Gets or sets the noise floor, in dB, passed to ffmpeg's silencedetect filter.
    /// </summary>
    public double HeuristicSilenceThresholdDb { get; set; } = -30;

    /// <summary>
    /// Gets or sets the pixel black threshold (0-1) passed to ffmpeg's blackdetect filter.
    /// </summary>
    public double HeuristicBlackPixelThreshold { get; set; } = 0.98;

    /// <summary>
    /// Gets or sets an explicit path to the ffmpeg executable. Empty means "look up ffmpeg on PATH".
    /// Jellyfin's own bundled ffmpeg is usually not on PATH; on the official Docker image it is at
    /// /usr/lib/jellyfin-ffmpeg/ffmpeg.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the maximum number of episodes per season that are fingerprinted and compared
    /// when looking for a recurring intro/outro. Later episodes in a season reuse the match found
    /// against this many earlier ones rather than being compared pairwise against every episode.
    /// </summary>
    public int MaxReferenceEpisodesPerSeason { get; set; } = 3;
}
