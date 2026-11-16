using System.Collections.Generic;
using Emby.Server.Implementations.Library.OptimizedVersions;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.OptimizedVersions;

public class OptimizedVersionPlanTests
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("mov,mp4,m4a,3gp,3g2,mj2")]
    public void Create_H264AacInMp4_NeedsNothing(string container)
    {
        var plan = OptimizedVersionPlan.Create(container, Video("h264"), Audio("aac", 2));

        Assert.False(plan.IsNeeded);
    }

    [Fact]
    public void Create_H264AacInMkv_OnlyChangesTheContainer()
    {
        var plan = OptimizedVersionPlan.Create("matroska,webm", Video("h264"), Audio("aac", 2));

        Assert.True(plan.IsNeeded);
        Assert.True(plan.CopyVideo);
        Assert.True(plan.CopyAudio);
    }

    [Theory]
    [InlineData("hevc", 8, 1920, 1080, false)]
    [InlineData("h264", 10, 1920, 1080, false)]
    [InlineData("h264", 8, 3840, 2160, false)]
    [InlineData("h264", 8, 1920, 1080, true)]
    public void Create_VideoPlayersCannotAllPlay_ConvertsTheVideo(string codec, int bitDepth, int width, int height, bool interlaced)
    {
        var video = Video(codec, width, height);
        video.BitDepth = bitDepth;
        video.IsInterlaced = interlaced;

        var plan = OptimizedVersionPlan.Create("mp4", video, Audio("aac", 2));

        Assert.True(plan.IsNeeded);
        Assert.False(plan.CopyVideo);
        Assert.True(plan.CopyAudio);
    }

    [Theory]
    [InlineData("ac3", 2)]
    [InlineData("aac", 6)]
    public void Create_AudioPlayersCannotAllPlay_ConvertsOnlyTheAudio(string codec, int channels)
    {
        var plan = OptimizedVersionPlan.Create("mp4", Video("h264"), Audio(codec, channels));

        Assert.True(plan.IsNeeded);
        Assert.True(plan.CopyVideo);
        Assert.False(plan.CopyAudio);
    }

    [Fact]
    public void Create_Hdr_IsSkippedRatherThanConvertedBadly()
    {
        var video = Video("hevc");
        video.BitDepth = 10;
        video.ColorTransfer = "smpte2084";
        Assert.Equal(VideoRange.HDR, video.VideoRange);

        var plan = OptimizedVersionPlan.Create("matroska", video, Audio("aac", 2));

        Assert.False(plan.IsNeeded);
        Assert.Contains("HDR", plan.SkipReason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NoVideoStream_IsSkipped()
    {
        Assert.False(OptimizedVersionPlan.Create("mp4", null, Audio("aac", 2)).IsNeeded);
    }

    [Fact]
    public void BuildArguments_ConvertingBoth_EncodesH264AndStereoAac()
    {
        var video = Video("hevc");
        var audio = Audio("eac3", 6);
        audio.Index = 2;
        var plan = OptimizedVersionPlan.Create("matroska", video, audio);

        var args = plan.BuildArguments("/media/in.mkv", "/data/out.partial", video, audio);

        AssertSequence(args, "-i", "/media/in.mkv");
        AssertSequence(args, "-map", "0:0", "-map", "0:2");
        AssertSequence(args, "-c:v", "libx264");
        AssertSequence(args, "-c:a", "aac", "-ac", "2");
        Assert.DoesNotContain("-vf", args);
        Assert.Equal(["-movflags", "+faststart", "-f", "mp4", "/data/out.partial"], Tail(args, 5));
    }

    [Fact]
    public void BuildArguments_CopyingBoth_DoesNotReencode()
    {
        var video = Video("h264");
        var audio = Audio("aac", 2);
        var plan = OptimizedVersionPlan.Create("matroska", video, audio);

        var args = plan.BuildArguments("in.mkv", "out", video, audio);

        AssertSequence(args, "-c:v", "copy");
        AssertSequence(args, "-c:a", "copy");
        Assert.DoesNotContain("libx264", args);
    }

    [Fact]
    public void BuildArguments_LargerThan1080p_ScalesDownKeepingTheShape()
    {
        var video = Video("h264", 3840, 2160);
        var plan = OptimizedVersionPlan.Create("mp4", video, null);

        var args = plan.BuildArguments("in.mp4", "out", video, null);

        AssertSequence(args, "-vf", "scale=w=1920:h=1080:force_original_aspect_ratio=decrease:force_divisible_by=2");
    }

    [Fact]
    public void BuildArguments_Interlaced_Deinterlaces()
    {
        var video = Video("mpeg2video", 720, 480);
        video.IsInterlaced = true;
        var plan = OptimizedVersionPlan.Create("mpegts", video, null);

        var args = plan.BuildArguments("in.ts", "out", video, null);

        AssertSequence(args, "-vf", "yadif");
    }

    [Fact]
    public void BuildArguments_NoAudio_MapsOnlyTheVideo()
    {
        var video = Video("hevc");
        var plan = OptimizedVersionPlan.Create("matroska", video, null);

        var args = plan.BuildArguments("in.mkv", "out", video, null);

        Assert.Single(args, a => a == "-map");
        Assert.DoesNotContain("-c:a", args);
    }

    private static MediaStream Video(string codec, int width = 1920, int height = 1080) => new()
    {
        Type = MediaStreamType.Video,
        Index = 0,
        Codec = codec,
        Width = width,
        Height = height,
        BitDepth = 8
    };

    private static MediaStream Audio(string codec, int channels) => new()
    {
        Type = MediaStreamType.Audio,
        Index = 1,
        Codec = codec,
        Channels = channels
    };

    private static void AssertSequence(IReadOnlyList<string> args, params string[] expected)
    {
        for (var start = 0; start + expected.Length <= args.Count; start++)
        {
            var match = true;
            for (var i = 0; i < expected.Length && match; i++)
            {
                match = args[start + i] == expected[i];
            }

            if (match)
            {
                return;
            }
        }

        Assert.Fail($"Expected the arguments to contain [{string.Join(' ', expected)}] but got [{string.Join(' ', args)}]");
    }

    private static List<string> Tail(IReadOnlyList<string> args, int count)
    {
        var tail = new List<string>();
        for (var i = args.Count - count; i < args.Count; i++)
        {
            tail.Add(args[i]);
        }

        return tail;
    }
}
