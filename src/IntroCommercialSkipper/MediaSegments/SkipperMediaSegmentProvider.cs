using Jellyfin.Plugin.IntroCommercialSkipper.Data;
using Jellyfin.Plugin.IntroCommercialSkipper.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper.MediaSegments;

/// <summary>
/// Supplies intro/outro/commercial segments to Jellyfin's built-in media segment system. Jellyfin
/// calls <see cref="GetMediaSegments"/> for every item from its own "Extract Media Segments"
/// scheduled task (run automatically, or manually from Dashboard &gt; Scheduled Tasks) and persists
/// whatever this provider returns - this class does not write to Jellyfin's database itself.
/// </summary>
public class SkipperMediaSegmentProvider : IMediaSegmentProvider
{
    private readonly ILibraryManager _libraryManager;
    private readonly SegmentDetectionService _detectionService;
    private readonly SegmentDatabase _cache;
    private readonly ILogger<SkipperMediaSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkipperMediaSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager, used to resolve the item being analyzed.</param>
    /// <param name="detectionService">Runs the actual intro/outro/commercial detection.</param>
    /// <param name="cache">Cache of previously detected segments, keyed by item and file state.</param>
    /// <param name="logger">Logger.</param>
    public SkipperMediaSegmentProvider(
        ILibraryManager libraryManager,
        SegmentDetectionService detectionService,
        SegmentDatabase cache,
        ILogger<SkipperMediaSegmentProvider> logger)
    {
        _libraryManager = libraryManager;
        _detectionService = detectionService;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Intro & Commercial Skipper";

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) =>
        ValueTask.FromResult(item.IsFileProtocol && !string.IsNullOrEmpty(item.Path) && File.Exists(item.Path));

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is null || !item.IsFileProtocol || string.IsNullOrEmpty(item.Path) || !File.Exists(item.Path))
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var fileInfo = new FileInfo(item.Path);
        var segments = _cache.TryGetFresh(item.Id, fileInfo.LastWriteTimeUtc, fileInfo.Length);

        if (segments is null)
        {
            try
            {
                segments = await _detectionService.DetectAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to detect segments for {Path}", item.Path);
                return Array.Empty<MediaSegmentDto>();
            }

            _cache.Store(item.Id, fileInfo.LastWriteTimeUtc, fileInfo.Length, segments);
        }

        return segments
            .Select(s => new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = item.Id,
                Type = s.Kind.ToMediaSegmentType(),
                StartTicks = s.Start.Ticks,
                EndTicks = s.End.Ticks,
            })
            .ToArray();
    }

    /// <inheritdoc />
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        _cache.Remove(itemId);
        return Task.CompletedTask;
    }
}
