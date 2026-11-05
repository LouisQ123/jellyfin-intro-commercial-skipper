namespace Jellyfin.Plugin.IntroCommercialSkipper.Data;

/// <summary>
/// A single skippable segment detected for a media item, in server-relative time.
/// </summary>
public class DetectedSegment
{
    /// <summary>Gets or sets the kind of segment.</summary>
    public SegmentKind Kind { get; set; }

    /// <summary>Gets or sets the segment start offset from the start of the file.</summary>
    public TimeSpan Start { get; set; }

    /// <summary>Gets or sets the segment end offset from the start of the file.</summary>
    public TimeSpan End { get; set; }

    /// <summary>
    /// Gets or sets how this segment was found. Used only for diagnostics/display, never sent to
    /// the server.
    /// </summary>
    public string Source { get; set; } = string.Empty;
}
