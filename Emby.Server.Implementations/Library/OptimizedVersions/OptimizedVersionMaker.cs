using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Makes the optimized version of one item. Only one is made at a time, whoever asks.
/// </summary>
public sealed class OptimizedVersionMaker : IOptimizedVersionMaker, IDisposable
{
    private readonly IMediaEncoder _mediaEncoder;
    private readonly OptimizedVersionStore _store;
    private readonly ILogger<OptimizedVersionMaker> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionMaker"/> class.
    /// </summary>
    /// <param name="mediaEncoder">The media encoder.</param>
    /// <param name="store">The optimized version store.</param>
    /// <param name="logger">The logger.</param>
    public OptimizedVersionMaker(IMediaEncoder mediaEncoder, OptimizedVersionStore store, ILogger<OptimizedVersionMaker> logger)
    {
        _mediaEncoder = mediaEncoder;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Plans the optimized version of an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The plan, or <c>null</c> if the item is not a video file one can be made of.</returns>
    public static OptimizedVersionPlan? Plan(BaseItem item)
    {
        if (!OptimizedVersionSelector.IsCandidate(item))
        {
            return null;
        }

        var (video, audio) = OptimizedVersion.PickStreams(item.GetMediaStreams());
        return OptimizedVersionPlan.Create(item.Container, video, audio);
    }

    /// <inheritdoc />
    public async Task<bool> MakeAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var plan = Plan(item);
        if (plan is null)
        {
            return false;
        }

        if (!plan.IsNeeded)
        {
            _logger.LogDebug("No optimized version for {Path}: {Reason}", item.Path, plan.SkipReason);
            return false;
        }

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_store.Find(item.Id, item.Path) is not null)
            {
                return false;
            }

            if (!_store.HasRoomFor(new FileInfo(item.Path).Length))
            {
                throw new IOException("Not enough free disk space for an optimized version of " + item.Path);
            }

            await MakeVersionAsync(item, plan, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _store.Delete(item.Id);
            throw;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _oneAtATime.Dispose();
    }

    private async Task MakeVersionAsync(BaseItem item, OptimizedVersionPlan plan, CancellationToken cancellationToken)
    {
        var (video, audio) = OptimizedVersion.PickStreams(item.GetMediaStreams());
        var source = new FileInfo(item.Path);
        var workingPath = _store.GetWorkingPath(item.Id);

        _logger.LogInformation(
            "Making an optimized version of {Path} (video: {Video}, audio: {Audio})",
            item.Path,
            plan.CopyVideo ? "copied" : "converted",
            audio is null ? "none" : plan.CopyAudio ? "copied" : "converted");

        await RunEncoderAsync(plan.BuildArguments(item.Path, workingPath, video!, audio), cancellationToken).ConfigureAwait(false);

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
}
