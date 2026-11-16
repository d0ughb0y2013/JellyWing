using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.OptimizedVersions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Makes an optimized version of every video that most players could not play as it is.
/// It has no default schedule: it runs when started or scheduled from the dashboard.
/// </summary>
public class OptimizedVersionsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IOptimizedVersionMaker _maker;
    private readonly OptimizedVersionStore _store;
    private readonly ILogger<OptimizedVersionsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="maker">The optimized version maker.</param>
    /// <param name="store">The optimized version store.</param>
    /// <param name="logger">The logger.</param>
    public OptimizedVersionsTask(
        ILibraryManager libraryManager,
        IOptimizedVersionMaker maker,
        OptimizedVersionStore store,
        ILogger<OptimizedVersionsTask> logger)
    {
        _libraryManager = libraryManager;
        _maker = maker;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Create optimized versions";

    /// <inheritdoc />
    public string Description => "Makes an H.264/AAC MP4 copy of each video that most players cannot play directly, so playing it later needs no transcoding. Uses a lot of processor time and disk space.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "OptimizedVersions";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        RemoveVersionsOfDeletedItems();

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video],
            IsVirtualItem = false,
            Recursive = true
        });

        var made = 0;
        var failed = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100d * i / items.Count);

            var item = items[i];
            if (_maker.Plan(item) is { IsNeeded: true }
                && _store.Find(item.Id, item.Path) is null
                && !_store.HasRoomFor(new FileInfo(item.Path).Length))
            {
                _logger.LogWarning("Stopping: not enough free disk space for more optimized versions.");
                break;
            }

            try
            {
                if (await _maker.MakeAsync(item, cancellationToken).ConfigureAwait(false))
                {
                    made++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogError(ex, "Could not make an optimized version of {Path}", item.Path);
            }
        }

        progress.Report(100);
        _logger.LogInformation("Optimized versions: {Made} made, {Failed} failed.", made, failed);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield break;
    }

    private void RemoveVersionsOfDeletedItems()
    {
        foreach (var itemId in _store.GetStoredItemIds())
        {
            if (_libraryManager.GetItemById(itemId) is null)
            {
                _logger.LogInformation("Removing the optimized version of deleted item {ItemId}", itemId);
                _store.Delete(itemId);
            }
        }
    }
}
