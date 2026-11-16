using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Identifies the optimized version of an item among its media sources.
/// </summary>
public static class OptimizedVersion
{
    /// <summary>
    /// Gets the media source id of an item's optimized version. It differs from the item id,
    /// which is the id of the original file's media source.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The media source id.</returns>
    public static string GetMediaSourceId(Guid itemId)
    {
#pragma warning disable CA5351 // Used to derive a stable id, not for security.
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("optimized:" + itemId.ToString("N", CultureInfo.InvariantCulture)));
#pragma warning restore CA5351
        return new Guid(hash).ToString("N", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Picks the streams of a file that its optimized version is made from.
    /// </summary>
    /// <param name="streams">The streams of the file.</param>
    /// <returns>The video stream and the default, or else first, audio stream; either may be missing.</returns>
    public static (MediaStream? Video, MediaStream? Audio) PickStreams(IEnumerable<MediaStream> streams)
    {
        ArgumentNullException.ThrowIfNull(streams);

        MediaStream? video = null;
        MediaStream? audio = null;
        foreach (var stream in streams)
        {
            if (stream.IsExternal)
            {
                continue;
            }

            if (stream.Type == MediaStreamType.Video)
            {
                video ??= stream;
            }
            else if (stream.Type == MediaStreamType.Audio && (audio is null || (stream.IsDefault && !audio.IsDefault)))
            {
                audio = stream;
            }
        }

        return (video, audio);
    }

    /// <summary>
    /// Gets a value indicating whether the optimized version holds everything that would be played
    /// from the original. It keeps one audio track and no subtitles, so it cannot stand in when
    /// subtitles are on or another audio track is selected.
    /// </summary>
    /// <param name="original">The original media source, with its selected stream indexes set.</param>
    /// <returns><c>true</c> if nothing selected would be lost.</returns>
    public static bool CoversSelection(MediaSourceInfo original)
    {
        ArgumentNullException.ThrowIfNull(original);

        if ((original.DefaultSubtitleStreamIndex ?? -1) >= 0)
        {
            return false;
        }

        var kept = PickStreams(original.MediaStreams ?? []).Audio;
        return original.DefaultAudioStreamIndex is null
            || kept is null
            || original.DefaultAudioStreamIndex == kept.Index;
    }
}
