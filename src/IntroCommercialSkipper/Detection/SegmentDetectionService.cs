using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.Plugin.IntroCommercialSkipper.Configuration;
using Jellyfin.Plugin.IntroCommercialSkipper.Data;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// Orchestrates this plugin's detection logic for a single media item: commercial breaks (via
/// Comskip .edl import or the heuristic fallback) for anything, and recurring intro/outro
/// detection (via cross-episode audio fingerprint correlation) for TV episodes.
/// </summary>
public class SegmentDetectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SegmentDetectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentDetectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager, used to look up sibling episodes for intro/outro comparison.</param>
    /// <param name="logger">Logger.</param>
    public SegmentDetectionService(ILibraryManager libraryManager, ILogger<SegmentDetectionService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Runs every detector this item's type and the current configuration call for.
    /// </summary>
    /// <param name="item">The media item to analyze. Must be a local file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All segments found, of any kind.</returns>
    public async Task<IReadOnlyList<DetectedSegment>> DetectAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var results = new List<DetectedSegment>();

        if (config.EnableCommercialDetection)
        {
            results.AddRange(await DetectCommercialsAsync(item.Path, config, cancellationToken).ConfigureAwait(false));
        }

        if (config.EnableIntroOutroDetection && item is Episode episode)
        {
            results.AddRange(await DetectIntroOutroAsync(episode, config, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<IReadOnlyList<DetectedSegment>> DetectCommercialsAsync(
        string path,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var fromEdl = ComskipEdlParser.TryParseAdjacentFile(path);
        if (fromEdl is not null)
        {
            _logger.LogDebug("Imported {Count} commercial segments from Comskip .edl for {Path}", fromEdl.Count, path);
            return fromEdl;
        }

        if (!config.EnableHeuristicCommercialFallback)
        {
            return Array.Empty<DetectedSegment>();
        }

        var detector = new BlackSilenceCommercialDetector(ExternalTools.ResolveFfmpeg(config), config, _logger);
        var heuristicResults = await detector.DetectAsync(path, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Heuristic black/silence scan found {Count} candidate commercial breaks for {Path}",
            heuristicResults.Count,
            path);
        return heuristicResults;
    }

    private async Task<IReadOnlyList<DetectedSegment>> DetectIntroOutroAsync(
        Episode episode,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (episode.Path is null || episode.SeasonId.IsEmpty())
        {
            return Array.Empty<DetectedSegment>();
        }

        var referenceEpisodes = GetReferenceEpisodes(episode, config.MaxReferenceEpisodesPerSeason);
        if (referenceEpisodes.Count == 0)
        {
            return Array.Empty<DetectedSegment>();
        }

        var fingerprinter = new AudioFingerprinter(ExternalTools.ResolveFfmpeg(config), _logger);
        var detector = new IntroOutroDetector(fingerprinter, config, _logger);

        var episodeDuration = TimeSpan.FromTicks(episode.RunTimeTicks ?? 0);
        var results = new List<DetectedSegment>();

        foreach (var reference in referenceEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (results.Any(s => s.Kind == SegmentKind.Intro) is false)
            {
                var introMatch = await detector
                    .FindIntroAsync(reference.Path, episode.Path, cancellationToken)
                    .ConfigureAwait(false);
                if (introMatch is not null)
                {
                    results.Add(new DetectedSegment
                    {
                        Kind = SegmentKind.Intro,
                        Start = introMatch.CandidateStart,
                        End = introMatch.CandidateEnd,
                        Source = $"fingerprint-vs:{reference.Id}",
                    });
                }
            }

            var referenceDuration = TimeSpan.FromTicks(reference.RunTimeTicks ?? 0);
            if (results.Any(s => s.Kind == SegmentKind.Outro) is false &&
                referenceDuration > TimeSpan.Zero &&
                episodeDuration > TimeSpan.Zero)
            {
                var outroMatch = await detector
                    .FindOutroAsync(reference.Path, episode.Path, referenceDuration, episodeDuration, cancellationToken)
                    .ConfigureAwait(false);
                if (outroMatch is not null)
                {
                    results.Add(new DetectedSegment
                    {
                        Kind = SegmentKind.Outro,
                        Start = outroMatch.CandidateStart,
                        End = outroMatch.CandidateEnd,
                        Source = $"fingerprint-vs:{reference.Id}",
                    });
                }
            }

            if (results.Any(s => s.Kind == SegmentKind.Intro) && results.Any(s => s.Kind == SegmentKind.Outro))
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Picks up to <paramref name="maxCount"/> other episodes from the same season to compare
    /// against, earliest-numbered first (the season premiere is the least likely episode to be
    /// missing its intro, e.g. because it cold-opens).
    /// </summary>
    private List<Episode> GetReferenceEpisodes(Episode episode, int maxCount)
    {
        var siblings = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = episode.SeasonId,
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = false,
            DtoOptions = new DtoOptions(false),
        });

        return siblings
            .OfType<Episode>()
            .Where(e => e.Id != episode.Id && e.IsFileProtocol && e.Path is not null && File.Exists(e.Path))
            .OrderBy(e => e.IndexNumber ?? int.MaxValue)
            .Take(maxCount)
            .ToList();
    }
}
