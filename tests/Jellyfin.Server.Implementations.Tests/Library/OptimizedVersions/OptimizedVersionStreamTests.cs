using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.OptimizedVersions;

public class OptimizedVersionStreamTests
{
    [Fact]
    public void PickStreams_PrefersTheDefaultAudioTrack()
    {
        MediaStream[] streams =
        [
            new() { Type = MediaStreamType.Video, Index = 0 },
            new() { Type = MediaStreamType.Audio, Index = 1 },
            new() { Type = MediaStreamType.Audio, Index = 2, IsDefault = true },
            new() { Type = MediaStreamType.Audio, Index = 3, IsDefault = true }
        ];

        var (video, audio) = OptimizedVersion.PickStreams(streams);

        Assert.Equal(0, video?.Index);
        Assert.Equal(2, audio?.Index);
    }

    [Fact]
    public void PickStreams_IgnoresExternalAndSubtitleStreams()
    {
        MediaStream[] streams =
        [
            new() { Type = MediaStreamType.Subtitle, Index = 0 },
            new() { Type = MediaStreamType.Audio, Index = 1, IsExternal = true },
            new() { Type = MediaStreamType.Video, Index = 2 }
        ];

        var (video, audio) = OptimizedVersion.PickStreams(streams);

        Assert.Equal(2, video?.Index);
        Assert.Null(audio);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(2, null, true)]
    [InlineData(2, -1, true)]
    [InlineData(1, null, false)]
    [InlineData(2, 3, false)]
    public void CoversSelection_OnlyWhenTheKeptAudioAndNoSubtitlesAreSelected(int? audioIndex, int? subtitleIndex, bool expected)
    {
        var original = new MediaBrowser.Model.Dto.MediaSourceInfo
        {
            MediaStreams =
            [
                new() { Type = MediaStreamType.Video, Index = 0 },
                new() { Type = MediaStreamType.Audio, Index = 1 },
                new() { Type = MediaStreamType.Audio, Index = 2, IsDefault = true },
                new() { Type = MediaStreamType.Subtitle, Index = 3 }
            ],
            DefaultAudioStreamIndex = audioIndex,
            DefaultSubtitleStreamIndex = subtitleIndex
        };

        Assert.Equal(expected, OptimizedVersion.CoversSelection(original));
    }
}
