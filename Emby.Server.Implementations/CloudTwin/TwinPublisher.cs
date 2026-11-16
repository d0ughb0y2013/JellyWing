using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.OptimizedVersions;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Publishes chosen libraries to the cloud twin's store: for each video one file that plays
/// anywhere, its picture, and a record describing it. Only what changed is sent.
/// </summary>
public class TwinPublisher
{
    private const string IndexKey = "catalog/index.json";

    private readonly ILibraryManager _libraryManager;
    private readonly IOptimizedVersionManager _optimizedVersions;
    private readonly IOptimizedVersionMaker _maker;
    private readonly ILogger<TwinPublisher> _logger;
    private readonly string _statePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="TwinPublisher"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="optimizedVersions">The optimized version manager.</param>
    /// <param name="maker">The optimized version maker.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public TwinPublisher(
        ILibraryManager libraryManager,
        IOptimizedVersionManager optimizedVersions,
        IOptimizedVersionMaker maker,
        IApplicationPaths applicationPaths,
        ILogger<TwinPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _libraryManager = libraryManager;
        _optimizedVersions = optimizedVersions;
        _maker = maker;
        _logger = logger;
        _statePath = Path.Combine(applicationPaths.DataPath, "cloud-twin", "state.json");
    }

    /// <summary>
    /// Publishes the named libraries and removes what is no longer in them.
    /// </summary>
    /// <param name="store">The store to publish to.</param>
    /// <param name="target">A name for the store; publishing to a different one starts over.</param>
    /// <param name="libraries">The names of the libraries to publish.</param>
    /// <param name="progress">Receives progress from 0 to 100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many items were sent, were already up to date, are waiting for an optimized version, and were removed.</returns>
    public async Task<(int Published, int Unchanged, int Waiting, int Removed)> PublishAsync(
        IObjectStore store,
        string target,
        IReadOnlyList<string> libraries,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(progress);

        var state = LoadState(target);
        var published = 0;
        var unchanged = 0;
        var waiting = 0;
        var current = new HashSet<Guid>();

        var items = GetItems(libraries);
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100d * i / items.Count);

            var (item, library) = items[i];
            string? mediaPath = null;
            if (item is Video)
            {
                mediaPath = GetPlayableFile(item, out var isWaiting);
                if (mediaPath is null)
                {
                    // Not published yet, but not gone either: what is already there stays.
                    waiting += isWaiting ? 1 : 0;
                    if (state.Items.ContainsKey(item.Id))
                    {
                        current.Add(item.Id);
                    }

                    continue;
                }
            }

            current.Add(item.Id);
            if (await PublishItemAsync(store, state, item, library, mediaPath, cancellationToken).ConfigureAwait(false))
            {
                published++;
                SaveState(state);
            }
            else
            {
                unchanged++;
            }
        }

        var removed = 0;
        foreach (var gone in state.Items.Keys.Where(id => !current.Contains(id)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RemoveItemAsync(store, state, gone, cancellationToken).ConfigureAwait(false);
            removed++;
        }

        if (published > 0 || removed > 0)
        {
            var index = new { Generated = DateTime.UtcNow, Libraries = libraries, Items = state.Items.Keys.Order().ToArray() };
            await store.PutBytesAsync(IndexKey, JsonSerializer.SerializeToUtf8Bytes(index, JsonDefaults.Options), "application/json", cancellationToken).ConfigureAwait(false);
            SaveState(state);
        }

        progress.Report(100);
        _logger.LogInformation(
            "Cloud twin: {Published} published, {Unchanged} unchanged, {Waiting} waiting for an optimized version, {Removed} removed.",
            published,
            unchanged,
            waiting,
            removed);
        return (published, unchanged, waiting, removed);
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return string.Create(CultureInfo.InvariantCulture, $"{file.Length}:{file.LastWriteTimeUtc.Ticks}");
    }

    private static string Name(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private List<(BaseItem Item, string Library)> GetItems(IReadOnlyList<string> libraries)
    {
        var items = new List<(BaseItem Item, string Library)>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (!libraries.Contains(folder.Name, StringComparer.OrdinalIgnoreCase) || !Guid.TryParse(folder.ItemId, out var libraryId))
            {
                continue;
            }

            var found = _libraryManager.GetItemList(new InternalItemsQuery
            {
                AncestorIds = [libraryId],
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode],
                IsVirtualItem = false,
                Recursive = true
            });
            items.AddRange(found.Select(item => (item, folder.Name)));
        }

        return items;
    }

    // The file the twin serves: the optimized version, or the original if that already plays everywhere.
    private string? GetPlayableFile(BaseItem item, out bool isWaiting)
    {
        isWaiting = false;
        if (_optimizedVersions.GetPath(item) is { } optimized)
        {
            return optimized;
        }

        var plan = _maker.Plan(item);
        if (plan is null)
        {
            return null;
        }

        if (plan.PlaysAsIs)
        {
            return item.Path;
        }

        if (plan.IsNeeded)
        {
            _optimizedVersions.Request(item);
            isWaiting = true;
        }

        return null;
    }

    private async Task<bool> PublishItemAsync(IObjectStore store, TwinPublishState state, BaseItem item, string library, string? mediaPath, CancellationToken cancellationToken)
    {
        state.Items.TryGetValue(item.Id, out var before);
        var after = new TwinPublishedItem();
        var changed = false;

        if (mediaPath is not null)
        {
            after.MediaKey = "media/" + Name(item.Id) + Path.GetExtension(mediaPath).ToLowerInvariant();
            after.MediaStamp = Stamp(mediaPath);
            if (before?.MediaKey != after.MediaKey || before.MediaStamp != after.MediaStamp)
            {
                await store.PutFileAsync(after.MediaKey, mediaPath, "video/mp4", cancellationToken).ConfigureAwait(false);
                changed = true;
            }
        }

        var imagePath = item.HasImage(ImageType.Primary, 0) ? item.GetImagePath(ImageType.Primary, 0) : null;
        if (imagePath is not null && File.Exists(imagePath))
        {
            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            after.ImageKey = "images/" + Name(item.Id) + extension;
            after.ImageStamp = Stamp(imagePath);
            if (before?.ImageKey != after.ImageKey || before.ImageStamp != after.ImageStamp)
            {
                var contentType = extension == ".png" ? "image/png" : extension == ".webp" ? "image/webp" : "image/jpeg";
                await store.PutFileAsync(after.ImageKey, imagePath, contentType, cancellationToken).ConfigureAwait(false);
                changed = true;
            }
        }

        var record = JsonSerializer.SerializeToUtf8Bytes(
            new TwinRecord
            {
                Id = item.Id,
                Type = item.GetBaseItemKind().ToString(),
                Name = item.Name,
                Library = library,
                SeriesId = item is Episode episode ? episode.SeriesId : (item as Season)?.SeriesId,
                SeasonId = (item as Episode)?.SeasonId,
                IndexNumber = item.IndexNumber,
                ParentIndexNumber = item.ParentIndexNumber,
                Overview = item.Overview,
                ProductionYear = item.ProductionYear,
                PremiereDate = item.PremiereDate,
                RunTimeTicks = item.RunTimeTicks,
                Genres = item.Genres ?? [],
                MediaKey = after.MediaKey,
                MediaSize = mediaPath is null ? null : new FileInfo(mediaPath).Length,
                ImageKey = after.ImageKey
            },
            JsonDefaults.Options);
        after.RecordHash = Convert.ToHexStringLower(SHA256.HashData(record));

        // The record goes last, so an item never appears before its file is there.
        if (before?.RecordHash != after.RecordHash)
        {
            await store.PutBytesAsync("catalog/" + Name(item.Id) + ".json", record, "application/json", cancellationToken).ConfigureAwait(false);
            changed = true;
        }

        // A replaced file may have a new name; the old one is not left behind.
        foreach (var stale in new[] { before?.MediaKey, before?.ImageKey })
        {
            if (stale is not null && stale != after.MediaKey && stale != after.ImageKey)
            {
                await store.DeleteAsync(stale, cancellationToken).ConfigureAwait(false);
            }
        }

        state.Items[item.Id] = after;
        return changed;
    }

    private async Task RemoveItemAsync(IObjectStore store, TwinPublishState state, Guid itemId, CancellationToken cancellationToken)
    {
        var item = state.Items[itemId];

        // The record goes first, so nothing points at a file that is gone.
        await store.DeleteAsync("catalog/" + Name(itemId) + ".json", cancellationToken).ConfigureAwait(false);
        foreach (var key in new[] { item.MediaKey, item.ImageKey })
        {
            if (key is not null)
            {
                await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }

        state.Items.Remove(itemId);
        SaveState(state);
    }

    private TwinPublishState LoadState(string target)
    {
        try
        {
            if (File.Exists(_statePath)
                && JsonSerializer.Deserialize<TwinPublishState>(File.ReadAllBytes(_statePath), JsonDefaults.Options) is { } state
                && string.Equals(state.Target, target, StringComparison.Ordinal))
            {
                return state;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The record of what was published could not be read; everything will be published again.");
        }

        return new TwinPublishState { Target = target };
    }

    private void SaveState(TwinPublishState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllBytes(_statePath, JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options));
    }
}
