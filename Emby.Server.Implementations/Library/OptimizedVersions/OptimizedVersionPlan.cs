using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Decides whether a video needs an optimized version and how ffmpeg should make it.
/// The target is H.264 video and stereo AAC audio in an MP4, at most 1080p: the combination
/// nearly every player handles without the server transcoding during playback.
/// </summary>
public sealed class OptimizedVersionPlan
{
    private const int MaxWidth = 1920;
    private const int MaxHeight = 1080;

    private static readonly string[] _compatibleContainers = ["mp4", "m4v", "mov"];

    private OptimizedVersionPlan(string? skipReason, bool copyVideo, bool copyAudio)
    {
        SkipReason = skipReason;
        CopyVideo = copyVideo;
        CopyAudio = copyAudio;
    }

    /// <summary>
    /// Gets why no optimized version should be made, or <c>null</c> if one is needed.
    /// </summary>
    public string? SkipReason { get; }

    /// <summary>
    /// Gets a value indicating whether an optimized version should be made.
    /// </summary>
    public bool IsNeeded => SkipReason is null;

    /// <summary>
    /// Gets a value indicating whether the video stream is kept as it is.
    /// </summary>
    public bool CopyVideo { get; }

    /// <summary>
    /// Gets a value indicating whether the audio stream is kept as it is.
    /// </summary>
    public bool CopyAudio { get; }

    /// <summary>
    /// Plans the optimized version of a file.
    /// </summary>
    /// <param name="container">The container, as probed; may list several names.</param>
    /// <param name="video">The video stream.</param>
    /// <param name="audio">The audio stream that will be kept, if the file has one.</param>
    /// <returns>The plan.</returns>
    public static OptimizedVersionPlan Create(string? container, MediaStream? video, MediaStream? audio)
    {
        if (video is null)
        {
            return new OptimizedVersionPlan("it has no video stream", false, false);
        }

        if (video.VideoRange == VideoRange.HDR)
        {
            // Converting HDR without tone mapping gives washed-out colors.
            return new OptimizedVersionPlan("it is HDR, which is not supported yet", false, false);
        }

        var copyVideo = string.Equals(video.Codec, "h264", StringComparison.OrdinalIgnoreCase)
            && (video.BitDepth ?? 8) <= 8
            && !video.IsInterlaced
            && (video.Width ?? 0) <= MaxWidth
            && (video.Height ?? 0) <= MaxHeight;

        var copyAudio = audio is null
            || (string.Equals(audio.Codec, "aac", StringComparison.OrdinalIgnoreCase) && (audio.Channels ?? 2) <= 2);

        if (copyVideo && copyAudio && IsCompatibleContainer(container))
        {
            return new OptimizedVersionPlan("it already plays everywhere", copyVideo, copyAudio);
        }

        return new OptimizedVersionPlan(null, copyVideo, copyAudio);
    }

    /// <summary>
    /// Builds the ffmpeg arguments that write the optimized version.
    /// </summary>
    /// <param name="inputPath">The source file.</param>
    /// <param name="outputPath">The file to write.</param>
    /// <param name="video">The video stream.</param>
    /// <param name="audio">The audio stream to keep, if any.</param>
    /// <returns>The arguments, one per element.</returns>
    public IReadOnlyList<string> BuildArguments(string inputPath, string outputPath, MediaStream video, MediaStream? audio)
    {
        ArgumentNullException.ThrowIfNull(video);

        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-i", inputPath,
            "-map", "0:" + video.Index.ToString(CultureInfo.InvariantCulture)
        };

        if (audio is not null)
        {
            args.AddRange(["-map", "0:" + audio.Index.ToString(CultureInfo.InvariantCulture)]);
        }

        args.AddRange(["-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "0"]);

        if (CopyVideo)
        {
            args.AddRange(["-c:v", "copy"]);
        }
        else
        {
            var filters = new List<string>();
            if (video.IsInterlaced)
            {
                filters.Add("yadif");
            }

            if ((video.Width ?? 0) > MaxWidth || (video.Height ?? 0) > MaxHeight)
            {
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"scale=w={MaxWidth}:h={MaxHeight}:force_original_aspect_ratio=decrease:force_divisible_by=2"));
            }

            if (filters.Count > 0)
            {
                args.AddRange(["-vf", string.Join(',', filters)]);
            }

            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p", "-profile:v", "high", "-level:v", "4.1"]);
        }

        if (audio is not null)
        {
            if (CopyAudio)
            {
                args.AddRange(["-c:a", "copy"]);
            }
            else
            {
                args.AddRange(["-c:a", "aac", "-ac", "2", "-b:a", "160k"]);
            }
        }

        args.AddRange(["-movflags", "+faststart", "-f", "mp4", outputPath]);
        return args;
    }

    private static bool IsCompatibleContainer(string? container)
    {
        if (string.IsNullOrEmpty(container))
        {
            return false;
        }

        foreach (var name in container.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.Exists(_compatibleContainers, c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
