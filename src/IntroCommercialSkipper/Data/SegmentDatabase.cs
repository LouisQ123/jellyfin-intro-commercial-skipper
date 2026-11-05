using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Data;

/// <summary>
/// A single cached detection result for one media item.
/// </summary>
public sealed class CacheEntry
{
    /// <summary>Gets or sets the file's last-write time at the time it was analyzed, used to detect a replaced file.</summary>
    public DateTime FileWriteTimeUtc { get; set; }

    /// <summary>Gets or sets the file's size at the time it was analyzed, used to detect a replaced file.</summary>
    public long FileSizeBytes { get; set; }

    /// <summary>Gets or sets the segments found for the file in that state.</summary>
    public List<DetectedSegment> Segments { get; set; } = new();
}

/// <summary>
/// Caches this plugin's own detection results (not Jellyfin's media segment records, which the
/// server persists itself once this plugin's provider returns them) so that re-running Jellyfin's
/// "Extract Media Segments" task doesn't re-fingerprint/re-scan every file every time it runs.
/// Persisted to a single JSON file under the plugin's data directory, keyed by media item id.
/// Deliberately simple (no SQLite dependency): entry counts are small (one per episode/movie) and
/// writes only happen right after a detection run, so a whole-file rewrite under a lock is more
/// than fast enough and avoids a native dependency.
/// </summary>
public class SegmentDatabase
{
    private readonly ILogger<SegmentDatabase> _logger;
    private readonly string _filePath;
    private readonly ConcurrentDictionary<Guid, CacheEntry> _entries = new();
    private readonly object _saveLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentDatabase"/> class and loads any
    /// previously persisted entries from <paramref name="dataDirectory"/>.
    /// </summary>
    /// <param name="dataDirectory">Directory to store the backing JSON file in.</param>
    /// <param name="logger">Logger.</param>
    public SegmentDatabase(string dataDirectory, ILogger<SegmentDatabase> logger)
    {
        _logger = logger;
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "segments.json");
        Load();
    }

    /// <summary>
    /// Returns the cached segments for an item, provided the file hasn't changed since they were
    /// computed.
    /// </summary>
    /// <param name="itemId">The media item id.</param>
    /// <param name="fileWriteTimeUtc">The file's current last-write time.</param>
    /// <param name="fileSizeBytes">The file's current size.</param>
    /// <returns>The cached segments, or null if there is no entry or the file has changed.</returns>
    public IReadOnlyList<DetectedSegment>? TryGetFresh(Guid itemId, DateTime fileWriteTimeUtc, long fileSizeBytes)
    {
        if (!_entries.TryGetValue(itemId, out var entry))
        {
            return null;
        }

        return entry.FileWriteTimeUtc == fileWriteTimeUtc && entry.FileSizeBytes == fileSizeBytes
            ? entry.Segments
            : null;
    }

    /// <summary>
    /// Stores (or replaces) the detection result for an item and persists the change.
    /// </summary>
    /// <param name="itemId">The media item id.</param>
    /// <param name="fileWriteTimeUtc">The file's last-write time at the time it was analyzed.</param>
    /// <param name="fileSizeBytes">The file's size at the time it was analyzed.</param>
    /// <param name="segments">The segments found.</param>
    public void Store(Guid itemId, DateTime fileWriteTimeUtc, long fileSizeBytes, IReadOnlyList<DetectedSegment> segments)
    {
        _entries[itemId] = new CacheEntry
        {
            FileWriteTimeUtc = fileWriteTimeUtc,
            FileSizeBytes = fileSizeBytes,
            Segments = segments.ToList(),
        };

        Save();
    }

    /// <summary>
    /// Removes a cached entry (for example, when Jellyfin tells this provider the item's segment
    /// data is being pruned) and persists the change.
    /// </summary>
    /// <param name="itemId">The media item id.</param>
    public void Remove(Guid itemId)
    {
        if (_entries.TryRemove(itemId, out _))
        {
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var dto = JsonSerializer.Deserialize<Dictionary<Guid, CacheEntry>>(json);
            if (dto is null)
            {
                return;
            }

            foreach (var (key, value) in dto)
            {
                _entries[key] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to load segment cache from {Path}; starting empty", _filePath);
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var snapshot = _entries.ToDictionary(kv => kv.Key, kv => kv.Value);
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = false });
                var tempPath = _filePath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to persist segment cache to {Path}", _filePath);
            }
        }
    }
}
