using System.Globalization;
using Jellyfin.Plugin.IntroCommercialSkipper.Data;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Detection;

/// <summary>
/// Reads Comskip-style .edl files: one cut per line, as whitespace-separated
/// "start_seconds end_seconds marker" (the marker, almost always "2" for Comskip, is ignored -
/// everything listed in the file is treated as a commercial break to skip).
/// </summary>
public static class ComskipEdlParser
{
    /// <summary>
    /// Looks for a Comskip .edl file next to <paramref name="mediaPath"/> (same path, ".edl"
    /// extension) and parses it if present.
    /// </summary>
    /// <param name="mediaPath">Path to the media file.</param>
    /// <returns>The parsed commercial segments, or null if no .edl file exists.</returns>
    public static IReadOnlyList<DetectedSegment>? TryParseAdjacentFile(string mediaPath)
    {
        var edlPath = Path.ChangeExtension(mediaPath, ".edl");
        if (!File.Exists(edlPath))
        {
            return null;
        }

        var segments = new List<DetectedSegment>();
        foreach (var line in File.ReadLines(edlPath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var startSeconds) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var endSeconds) ||
                endSeconds <= startSeconds)
            {
                continue;
            }

            segments.Add(new DetectedSegment
            {
                Kind = SegmentKind.Commercial,
                Start = TimeSpan.FromSeconds(startSeconds),
                End = TimeSpan.FromSeconds(endSeconds),
                Source = "comskip-edl",
            });
        }

        return segments;
    }
}
