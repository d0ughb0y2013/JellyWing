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
    // The frame must fit a 1080p screen held either way up, so a portrait video is not shrunk.
    private const int MaxLongSide = 1920;
    private const int MaxShortSide = 1080;

    private static readonly string[] _compatibleContainers = ["mp4", "m4v", "mov"];

    // HDR formats whose picture can be converted to SDR from the stream itself. Dolby Vision
    // without an HDR10 or HLG base needs its own processing and is left alone.
    private static readonly VideoRangeType[] _toneMappable =
    [
        VideoRangeType.HDR10,
        VideoRangeType.HDR10Plus,
        VideoRangeType.HLG,
        VideoRangeType.DOVIWithHDR10,
        VideoRangeType.DOVIWithHDR10Plus,
        VideoRangeType.DOVIWithHLG
    ];

    private OptimizedVersionPlan(string? skipReason, bool copyVideo, bool copyAudio, bool toneMap = false, bool playsAsIs = false)
    {
        SkipReason = skipReason;
        CopyVideo = copyVideo;
        CopyAudio = copyAudio;
        ToneMap = toneMap;
        PlaysAsIs = playsAsIs;
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
    /// Gets a value indicating whether the file needs no optimized version because it already
    /// plays everywhere, as opposed to one that cannot be made.
    /// </summary>
    public bool PlaysAsIs { get; }

    /// <summary>
    /// Gets a value indicating whether the video stream is kept as it is.
    /// </summary>
    public bool CopyVideo { get; }

    /// <summary>
    /// Gets a value indicating whether the audio stream is kept as it is.
    /// </summary>
    public bool CopyAudio { get; }

    /// <summary>
    /// Gets a value indicating whether the HDR picture is converted to SDR.
    /// </summary>
    public bool ToneMap { get; }

    /// <summary>
    /// Plans the optimized version of a file.
    /// </summary>
    /// <param name="container">The container, as probed; may list several names.</param>
    /// <param name="video">The video stream.</param>
    /// <param name="audio">The audio stream that will be kept, if the file has one.</param>
    /// <param name="canToneMap">Whether the encoder can convert HDR to SDR.</param>
    /// <returns>The plan.</returns>
    public static OptimizedVersionPlan Create(string? container, MediaStream? video, MediaStream? audio, bool canToneMap = false)
    {
        if (video is null)
        {
            return new OptimizedVersionPlan("it has no video stream", false, false);
        }

        var copyAudio = audio is null
            || (string.Equals(audio.Codec, "aac", StringComparison.OrdinalIgnoreCase) && (audio.Channels ?? 2) <= 2);

        if (video.VideoRange == VideoRange.HDR)
        {
            // Converting HDR without tone mapping gives washed-out colors, so it is that or nothing.
            if (!canToneMap)
            {
                return new OptimizedVersionPlan("it is HDR and this ffmpeg cannot convert HDR", false, false);
            }

            if (Array.IndexOf(_toneMappable, video.VideoRangeType) < 0)
            {
                return new OptimizedVersionPlan("its kind of HDR cannot be converted", false, false);
            }

            return new OptimizedVersionPlan(null, false, copyAudio, toneMap: true);
        }

        var copyVideo = string.Equals(video.Codec, "h264", StringComparison.OrdinalIgnoreCase)
            && (video.BitDepth ?? 8) <= 8
            && !video.IsInterlaced
            && FitsTheFrame(video);

        if (copyVideo && copyAudio && IsCompatibleContainer(container))
        {
            return new OptimizedVersionPlan("it already plays everywhere", copyVideo, copyAudio, playsAsIs: true);
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

            if (!FitsTheFrame(video))
            {
                var portrait = (video.Height ?? 0) > (video.Width ?? 0);
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"scale=w={(portrait ? MaxShortSide : MaxLongSide)}:h={(portrait ? MaxLongSide : MaxShortSide)}:force_original_aspect_ratio=decrease:force_divisible_by=2"));
            }

            if (ToneMap)
            {
                // The same conversion the server uses when it tone maps in software during playback.
                filters.Add("tonemapx=tonemap=bt2390:desat=0:peak=100:t=bt709:m=bt709:p=bt709:format=yuv420p");
            }

            if (filters.Count > 0)
            {
                args.AddRange(["-vf", string.Join(',', filters)]);
            }

            if (ToneMap)
            {
                args.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
            }

            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "22", "-pix_fmt", "yuv420p", "-profile:v", "high"]);
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

    private static bool FitsTheFrame(MediaStream video)
    {
        var width = video.Width ?? 0;
        var height = video.Height ?? 0;
        return Math.Max(width, height) <= MaxLongSide && Math.Min(width, height) <= MaxShortSide;
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
