using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Extensions;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// After each library scan, asks for an optimized version of every video that needs one
/// and has none. Off unless turned on, since it uses a lot of processor time and disk space.
/// </summary>
public class OptimizedVersionsPostScanTask : ILibraryPostScanTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IOptimizedVersionManager _manager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OptimizedVersionsPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionsPostScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="manager">The optimized version manager.</param>
    /// <param name="configuration">The startup configuration.</param>
    /// <param name="logger">The logger.</param>
    public OptimizedVersionsPostScanTask(
        ILibraryManager libraryManager,
        IOptimizedVersionManager manager,
        IConfiguration configuration,
        ILogger<OptimizedVersionsPostScanTask> logger)
    {
        _libraryManager = libraryManager;
        _manager = manager;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_configuration.GetOptimizeNewVideos())
        {
            return Task.CompletedTask;
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video],
            IsVirtualItem = false,
            Recursive = true
        });

        var requested = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_manager.GetStatus(items[i]).State == OptimizedVersionState.None
                && _manager.Request(items[i]).State == OptimizedVersionState.Queued)
            {
                requested++;
            }

            progress.Report(100d * (i + 1) / items.Count);
        }

        if (requested > 0)
        {
            _logger.LogInformation("Asked for optimized versions of {Count} videos found by the scan.", requested);
        }

        return Task.CompletedTask;
    }
}
