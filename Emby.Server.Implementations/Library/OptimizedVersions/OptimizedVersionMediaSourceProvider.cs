using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Offers an item's optimized version to players as an additional version of the item.
/// </summary>
public class OptimizedVersionMediaSourceProvider : IMediaSourceProvider
{
    /// <summary>
    /// The name players show for the optimized version.
    /// </summary>
    public const string VersionName = "Optimized";

    private readonly OptimizedVersionStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionMediaSourceProvider"/> class.
    /// </summary>
    /// <param name="store">The optimized version store.</param>
    public OptimizedVersionMediaSourceProvider(OptimizedVersionStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (!OptimizedVersionSelector.IsCandidate(item))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var info = _store.Find(item.Id, item.Path);
        if (info is null)
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        IEnumerable<MediaSourceInfo> sources =
        [
            new MediaSourceInfo
            {
                Id = OptimizedVersion.GetMediaSourceId(item.Id),
                Name = VersionName,
                Path = _store.GetMediaPath(item.Id),
                Protocol = MediaProtocol.File,
                Type = MediaSourceType.Default,
                VideoType = VideoType.VideoFile,
                Container = "mp4",
                Size = info.Size,
                RunTimeTicks = item.RunTimeTicks,
                MediaStreams = info.MediaStreams,
                IsRemote = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true
            }
        ];

        return Task.FromResult(sources);
    }

    /// <inheritdoc />
    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // An optimized version is an ordinary file and is never opened as a live stream.
        throw new NotSupportedException();
    }
}
