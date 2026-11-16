using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Picks which videos optimized versions are made from.
/// </summary>
public static class OptimizedVersionSelector
{
    /// <summary>
    /// Gets a value indicating whether an item is a plain video file an optimized version can be made of.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c> for a real, local video file that is not an alternate version of another item.</returns>
    public static bool IsCandidate(BaseItem item)
    {
        return item is Video video
            && video.VideoType == VideoType.VideoFile
            && !video.IsVirtualItem
            && !video.IsPlaceHolder
            && !video.IsShortcut
            && !video.PrimaryVersionId.HasValue
            && !string.IsNullOrEmpty(video.Path)
            && Path.IsPathFullyQualified(video.Path);
    }
}
