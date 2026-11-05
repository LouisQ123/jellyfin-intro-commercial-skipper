namespace Jellyfin.Plugin.IntroCommercialSkipper.Data;

/// <summary>
/// The kind of skippable segment this plugin detected. Maps onto Jellyfin's own
/// MediaBrowser.Model.MediaSegments.MediaSegmentType when segments are handed to the server.
/// </summary>
public enum SegmentKind
{
    /// <summary>A recurring, repeated-across-episodes intro/opening sequence.</summary>
    Intro,

    /// <summary>A recurring, repeated-across-episodes outro/end-credits sequence.</summary>
    Outro,

    /// <summary>An inserted commercial break (recorded broadcast content).</summary>
    Commercial,
}
