using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Makes optimized versions of single items on request, one after another, in the background.
/// What is waiting is held in memory only: a restart forgets it, and the item can be asked for again.
/// </summary>
public class OptimizedVersionManager : IOptimizedVersionManager
{
    private readonly ILibraryManager _libraryManager;
    private readonly OptimizedVersionStore _store;
    private readonly IOptimizedVersionMaker _maker;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<OptimizedVersionManager> _logger;

    private readonly ConcurrentDictionary<Guid, OptimizedVersionState> _states = new();
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly Lock _workerLock = new();
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

        var plan = OptimizedVersionMaker.Plan(item);
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

    /// <inheritdoc />
    public (OptimizedVersionState State, long? Size) Request(BaseItem item)
    {
        var status = GetStatus(item);
        if (status.State is not (OptimizedVersionState.None or OptimizedVersionState.Failed))
        {
            return status;
        }

        _states[item.Id] = OptimizedVersionState.Queued;
        _queue.Writer.TryWrite(item.Id);
        EnsureWorker();
        return (OptimizedVersionState.Queued, null);
    }

    /// <inheritdoc />
    public string? GetPath(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return OptimizedVersionSelector.IsCandidate(item) && _store.Find(item.Id, item.Path) is not null
            ? _store.GetMediaPath(item.Id)
            : null;
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
            }
        }
        catch (OperationCanceledException)
        {
            // The server is stopping.
        }
    }
}
