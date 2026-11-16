using System.IO;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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

    /// <summary>
    /// Plans the optimized version of an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="canToneMap">Whether the encoder can convert HDR to SDR.</param>
    /// <returns>The plan, or <c>null</c> if the item is not a video file one can be made of.</returns>
    public static OptimizedVersionPlan? Plan(BaseItem item, bool canToneMap)
    {
        if (!IsCandidate(item))
        {
            return null;
        }

        var (video, audio) = OptimizedVersion.PickStreams(item.GetMediaStreams());
        return OptimizedVersionPlan.Create(item.Container, video, audio, canToneMap);
    }
}
