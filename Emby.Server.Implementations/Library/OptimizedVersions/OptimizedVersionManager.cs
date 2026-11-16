using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Makes optimized versions on request, one after another, in the background. A request for
/// a folder, such as a season or a series, is a request for every video in it. What is
/// waiting is written to a file so that a restart picks up where it left off.
/// </summary>
public class OptimizedVersionManager : IOptimizedVersionManager
{
    private static readonly BaseItemKind[] _videoKinds =
        [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video];

    private readonly ILibraryManager _libraryManager;
    private readonly OptimizedVersionStore _store;
    private readonly IOptimizedVersionMaker _maker;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<OptimizedVersionManager> _logger;

    private readonly ConcurrentDictionary<Guid, OptimizedVersionState> _states = new();
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly Lock _workerLock = new();
    private readonly Lock _queueFileLock = new();
    private Task? _worker;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionManager"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="store">The optimized version store.</param>
    /// <param name="maker">The optimized version maker.</param>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public OptimizedVersionManager(
        ILibraryManager libraryManager,
        OptimizedVersionStore store,
        IOptimizedVersionMaker maker,
        IHostApplicationLifetime lifetime,
        ILogger<OptimizedVersionManager> logger)
    {
        _libraryManager = libraryManager;
        _store = store;
        _maker = maker;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public (OptimizedVersionState State, long? Size) GetStatus(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return item is Folder folder ? GetFolderStatus(folder) : GetVideoStatus(item);
    }

    /// <inheritdoc />
    public (OptimizedVersionState State, long? Size) Request(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is Folder folder)
        {
            foreach (var video in GetVideos(folder))
            {
                RequestVideo(video);
            }
        }
        else
        {
            RequestVideo(item);
        }

        SaveQueue();
        return GetStatus(item);
    }

    /// <inheritdoc />
    public string? GetPath(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return OptimizedVersionSelector.IsCandidate(item) && _store.Find(item.Id, item.Path) is not null
            ? _store.GetMediaPath(item.Id)
            : null;
    }

    /// <inheritdoc />
    public bool Delete(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // The file being written belongs to the worker until it is done.
        if (_states.TryGetValue(item.Id, out var state) && state == OptimizedVersionState.Running)
        {
            return false;
        }

        var existed = File.Exists(_store.GetMediaPath(item.Id));
        _store.Delete(item.Id);
        return existed;
    }

    /// <inheritdoc />
    public (int Count, long TotalSize) GetSummary() => _store.GetTotals();

    /// <inheritdoc />
    public int Resume()
    {
        Guid[]? waiting;
        try
        {
            waiting = File.Exists(_store.QueuePath)
                ? JsonSerializer.Deserialize<Guid[]>(File.ReadAllBytes(_store.QueuePath), JsonDefaults.Options)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "The list of waiting optimized versions could not be read and is ignored.");
            return 0;
        }

        var resumed = 0;
        foreach (var itemId in waiting ?? [])
        {
            if (_libraryManager.GetItemById(itemId) is { } item && RequestVideo(item))
            {
                resumed++;
            }
        }

        SaveQueue();
        return resumed;
    }

    private (OptimizedVersionState State, long? Size) GetVideoStatus(BaseItem item)
    {
        var plan = _maker.Plan(item);
        if (plan is null)
        {
            return (OptimizedVersionState.NotNeeded, null);
        }

        if (_store.Find(item.Id, item.Path) is { } info)
        {
            return (OptimizedVersionState.Ready, info.Size);
        }

        if (_states.TryGetValue(item.Id, out var state))
        {
            return (state, null);
        }

        return (plan.IsNeeded ? OptimizedVersionState.None : OptimizedVersionState.NotNeeded, null);
    }

    // A folder is as far along as the videos in it that need a copy.
    private (OptimizedVersionState State, long? Size) GetFolderStatus(Folder folder)
    {
        var needed = 0;
        var ready = 0;
        var failed = 0;
        var queued = 0;
        var running = 0;
        long size = 0;
        foreach (var video in GetVideos(folder))
        {
            var (state, videoSize) = GetVideoStatus(video);
            if (state == OptimizedVersionState.NotNeeded)
            {
                continue;
            }

            needed++;
            switch (state)
            {
                case OptimizedVersionState.Ready:
                    ready++;
                    size += videoSize ?? 0;
                    break;
                case OptimizedVersionState.Running:
                    running++;
                    break;
                case OptimizedVersionState.Queued:
                    queued++;
                    break;
                case OptimizedVersionState.Failed:
                    failed++;
                    break;
            }
        }

        long? readySize = ready > 0 ? size : null;
        if (needed == 0)
        {
            return (OptimizedVersionState.NotNeeded, null);
        }

        if (running > 0)
        {
            return (OptimizedVersionState.Running, readySize);
        }

        if (queued > 0)
        {
            return (OptimizedVersionState.Queued, readySize);
        }

        if (ready == needed)
        {
            return (OptimizedVersionState.Ready, readySize);
        }

        return (ready + failed == needed ? OptimizedVersionState.Failed : OptimizedVersionState.None, readySize);
    }

    private IReadOnlyList<BaseItem> GetVideos(Folder folder)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            AncestorIds = [folder.Id],
            IncludeItemTypes = _videoKinds,
            IsVirtualItem = false,
            Recursive = true
        });

    private bool RequestVideo(BaseItem item)
    {
        if (GetVideoStatus(item).State is not (OptimizedVersionState.None or OptimizedVersionState.Failed))
        {
            return false;
        }

        _states[item.Id] = OptimizedVersionState.Queued;
        _queue.Writer.TryWrite(item.Id);
        EnsureWorker();
        return true;
    }

    private void EnsureWorker()
    {
        lock (_workerLock)
        {
            _worker ??= Task.Run(() => WorkAsync(_lifetime.ApplicationStopping), CancellationToken.None);
        }
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var itemId in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var item = _libraryManager.GetItemById(itemId);
                if (item is null)
                {
                    _states.TryRemove(itemId, out _);
                    SaveQueue();
                    continue;
                }

                _states[itemId] = OptimizedVersionState.Running;
                try
                {
                    await _maker.MakeAsync(item, cancellationToken).ConfigureAwait(false);
                    _states.TryRemove(itemId, out _);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _states[itemId] = OptimizedVersionState.Failed;
                    _logger.LogError(ex, "Could not make the requested optimized version of {Path}", item.Path);
                }

                SaveQueue();
            }
        }
        catch (OperationCanceledException)
        {
            // The server is stopping; what was waiting stays in the queue file for next time.
        }
    }

    // Best effort: losing this file only means a waiting request has to be made again.
    private void SaveQueue()
    {
        var waiting = _states
            .Where(s => s.Value is OptimizedVersionState.Queued or OptimizedVersionState.Running)
            .Select(s => s.Key)
            .ToArray();

        lock (_queueFileLock)
        {
            try
            {
                if (waiting.Length == 0)
                {
                    File.Delete(_store.QueuePath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_store.QueuePath)!);
                    File.WriteAllBytes(_store.QueuePath, JsonSerializer.SerializeToUtf8Bytes(waiting, JsonDefaults.Options));
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "The list of waiting optimized versions could not be saved.");
            }
        }
    }
}
