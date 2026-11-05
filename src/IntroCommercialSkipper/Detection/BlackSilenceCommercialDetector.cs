using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.IntroCommercialSkipper.Configuration;
using Jellyfin.Plugin.IntroCommercialSkipper.Data;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// Best-effort commercial-break detector for recorded broadcast content that has no Comskip .edl
/// file available. Looks for "blank" points - moments where the picture goes black and the audio
/// goes silent at the same time, which is how most broadcasters mark the transition into and out
/// of an ad break - and treats a gap between two blank points of a length typical for a commercial
/// pod as a candidate break.
/// </summary>
/// <remarks>
/// This is a heuristic, not a replacement for Comskip: Comskip also uses logo detection, frame
/// differencing and channel-specific tuning that this detector does not attempt. It exists for
/// users who have no Comskip pipeline at all; <see cref="ComskipEdlParser"/> is always preferred
/// when a .edl file is present.
/// </remarks>
public class BlackSilenceCommercialDetector
{
    private static readonly Regex BlackRegex = new(
        @"black_start:(?<start>[\d.]+) black_end:(?<end>[\d.]+)",
        RegexOptions.Compiled);

    private static readonly Regex SilenceStartRegex = new(
        @"silence_start:\s*(?<start>[\d.]+)",
        RegexOptions.Compiled);

    private static readonly Regex SilenceEndRegex = new(
        @"silence_end:\s*(?<end>[\d.]+)",
        RegexOptions.Compiled);

    /// <summary>Shortest gap between two blank points still considered a plausible commercial pod.</summary>
    private static readonly TimeSpan MinPodLength = TimeSpan.FromSeconds(15);

    /// <summary>Longest gap between two blank points still considered a plausible commercial pod.</summary>
    private static readonly TimeSpan MaxPodLength = TimeSpan.FromSeconds(210);

    private readonly string _ffmpegPath;
    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BlackSilenceCommercialDetector"/> class.
    /// </summary>
    /// <param name="ffmpegPath">Path to, or bare command name of, the ffmpeg executable.</param>
    /// <param name="config">Plugin configuration (heuristic thresholds).</param>
    /// <param name="logger">Logger.</param>
    public BlackSilenceCommercialDetector(string ffmpegPath, PluginConfiguration config, ILogger logger)
    {
        _ffmpegPath = ffmpegPath;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Scans a media file for candidate commercial breaks.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Candidate commercial segments, lowest confidence first; may be empty.</returns>
    public async Task<IReadOnlyList<DetectedSegment>> DetectAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var stderr = await RunAnalysisAsync(mediaPath, cancellationToken).ConfigureAwait(false);
        if (stderr is null)
        {
            return Array.Empty<DetectedSegment>();
        }

        var blackIntervals = ParseBlackIntervals(stderr);
        var silenceIntervals = ParseSilenceIntervals(stderr);
        var blankPoints = FindBlankPoints(blackIntervals, silenceIntervals);

        return InferCommercialPods(blankPoints);
    }

    private async Task<string?> RunAnalysisAsync(string mediaPath, CancellationToken cancellationToken)
    {
        var minDuration = _config.HeuristicMinBreakSeconds.ToString(CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(_ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(mediaPath);
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(
            $"blackdetect=d={minDuration}:pic_th={_config.HeuristicBlackPixelThreshold.ToString(CultureInfo.InvariantCulture)}");
        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add(
            $"silencedetect=noise={_config.HeuristicSilenceThresholdDb.ToString(CultureInfo.InvariantCulture)}dB:d={minDuration}");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ExternalToolNotFoundException(_ffmpegPath, ex);
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            try
            {
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
                var drainStdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
                await Task.WhenAll(stderrTask, drainStdout, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
                return await stderrTask.ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to run ffmpeg black/silence analysis for {Path}", mediaPath);
                return null;
            }
        }
    }

    private static List<(TimeSpan Start, TimeSpan End)> ParseBlackIntervals(string ffmpegStderr)
    {
        var intervals = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (Match m in BlackRegex.Matches(ffmpegStderr))
        {
            var start = double.Parse(m.Groups["start"].Value, CultureInfo.InvariantCulture);
            var end = double.Parse(m.Groups["end"].Value, CultureInfo.InvariantCulture);
            intervals.Add((TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end)));
        }

        return intervals;
    }

    private static List<(TimeSpan Start, TimeSpan End)> ParseSilenceIntervals(string ffmpegStderr)
    {
        var intervals = new List<(TimeSpan Start, TimeSpan End)>();
        double? pendingStart = null;

        foreach (var line in ffmpegStderr.Split('\n'))
        {
            var startMatch = SilenceStartRegex.Match(line);
            if (startMatch.Success)
            {
                pendingStart = double.Parse(startMatch.Groups["start"].Value, CultureInfo.InvariantCulture);
                continue;
            }

            var endMatch = SilenceEndRegex.Match(line);
            if (endMatch.Success && pendingStart.HasValue)
            {
                var end = double.Parse(endMatch.Groups["end"].Value, CultureInfo.InvariantCulture);
                intervals.Add((TimeSpan.FromSeconds(pendingStart.Value), TimeSpan.FromSeconds(end)));
                pendingStart = null;
            }
        }

        return intervals;
    }

    /// <summary>
    /// A "blank point" is a moment where a black interval and a silence interval overlap -
    /// treated as the single timestamp where picture and sound both cut, taken as the midpoint of
    /// the overlap.
    /// </summary>
    private static List<TimeSpan> FindBlankPoints(
        List<(TimeSpan Start, TimeSpan End)> blackIntervals,
        List<(TimeSpan Start, TimeSpan End)> silenceIntervals)
    {
        var points = new List<TimeSpan>();
        foreach (var black in blackIntervals)
        {
            foreach (var silence in silenceIntervals)
            {
                var overlapStart = black.Start > silence.Start ? black.Start : silence.Start;
                var overlapEnd = black.End < silence.End ? black.End : silence.End;
                if (overlapEnd > overlapStart)
                {
                    points.Add(overlapStart + ((overlapEnd - overlapStart) / 2));
                }
            }
        }

        points.Sort();
        return points;
    }

    private static List<DetectedSegment> InferCommercialPods(List<TimeSpan> blankPoints)
    {
        var pods = new List<DetectedSegment>();
        for (var i = 0; i < blankPoints.Count - 1; i++)
        {
            var gap = blankPoints[i + 1] - blankPoints[i];
            if (gap >= MinPodLength && gap <= MaxPodLength)
            {
                pods.Add(new DetectedSegment
                {
                    Kind = SegmentKind.Commercial,
                    Start = blankPoints[i],
                    End = blankPoints[i + 1],
                    Source = "heuristic-black-silence",
                });
            }
        }

        return pods;
    }
}
