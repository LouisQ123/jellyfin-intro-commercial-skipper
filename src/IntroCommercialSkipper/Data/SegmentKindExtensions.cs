using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Plugin.IntroCommercialSkipper.Data;

/// <summary>
/// Maps this plugin's own <see cref="SegmentKind"/> onto the server's <see cref="MediaSegmentType"/>.
/// </summary>
public static class SegmentKindExtensions
{
    /// <summary>
    /// Converts a <see cref="SegmentKind"/> into the equivalent server-side <see cref="MediaSegmentType"/>.
    /// </summary>
    /// <param name="kind">The kind to convert.</param>
    /// <returns>The equivalent <see cref="MediaSegmentType"/>.</returns>
    public static MediaSegmentType ToMediaSegmentType(this SegmentKind kind) => kind switch
    {
        SegmentKind.Intro => MediaSegmentType.Intro,
        SegmentKind.Outro => MediaSegmentType.Outro,
        SegmentKind.Commercial => MediaSegmentType.Commercial,
        _ => MediaSegmentType.Unknown,
    };
}
