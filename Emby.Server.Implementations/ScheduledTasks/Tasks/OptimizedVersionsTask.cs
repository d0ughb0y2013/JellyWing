using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.OptimizedVersions;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
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
    private readonly IMediaEncoder _mediaEncoder;
    private readonly OptimizedVersionStore _store;
    private readonly ILogger<OptimizedVersionsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionsTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="store">The optimized version store.</param>
    /// <param name="logger">The logger.</param>
    public OptimizedVersionsTask(
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        OptimizedVersionStore store,
        ILogger<OptimizedVersionsTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
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
            if (!OptimizedVersionSelector.IsCandidate(item) || _store.Find(item.Id, item.Path) is not null)
            {
                continue;
            }

            var (video, audio) = OptimizedVersion.PickStreams(item.GetMediaStreams());
            var plan = OptimizedVersionPlan.Create(item.Container, video, audio);
            if (!plan.IsNeeded)
            {
                _logger.LogDebug("No optimized version for {Path}: {Reason}", item.Path, plan.SkipReason);
                continue;
            }

            try
            {
                await MakeVersionAsync(item, plan, video!, audio, cancellationToken).ConfigureAwait(false);
                made++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _store.Delete(item.Id);
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

    private async Task MakeVersionAsync(
        BaseItem item,
        OptimizedVersionPlan plan,
        MediaBrowser.Model.Entities.MediaStream video,
        MediaBrowser.Model.Entities.MediaStream? audio,
        CancellationToken cancellationToken)
    {
        var source = new FileInfo(item.Path);
        var workingPath = _store.GetWorkingPath(item.Id);

        _logger.LogInformation(
            "Making an optimized version of {Path} (video: {Video}, audio: {Audio})",
            item.Path,
            plan.CopyVideo ? "copied" : "converted",
            audio is null ? "none" : plan.CopyAudio ? "copied" : "converted");

        try
        {
            await RunEncoderAsync(plan.BuildArguments(item.Path, workingPath, video, audio), cancellationToken).ConfigureAwait(false);

            var probe = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo { Path = workingPath, Protocol = MediaProtocol.File },
                    MediaType = DlnaProfileType.Video
                },
                cancellationToken).ConfigureAwait(false);

            _store.Save(item.Id, workingPath, new OptimizedVersionInfo
            {
                SourcePath = item.Path,
                SourceSize = source.Length,
                SourceModifiedTicks = source.LastWriteTimeUtc.Ticks,
                Size = new FileInfo(workingPath).Length,
                Created = DateTime.UtcNow,
                MediaStreams = probe.MediaStreams
            });
        }
        catch (OperationCanceledException)
        {
            _store.Delete(item.Id);
            throw;
        }
    }

    private async Task RunEncoderAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}: {(await errors.ConfigureAwait(false)).Trim()}");
        }
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
