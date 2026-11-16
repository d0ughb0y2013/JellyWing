using System;
using System.Collections.Generic;
using MediaBrowser.Model.Entities;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// What is recorded beside an optimized version: which source it was made from and what it contains.
/// </summary>
public class OptimizedVersionInfo
{
    /// <summary>
    /// Gets or sets the path of the file it was made from.
    /// </summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size of the source file when it was made.
    /// </summary>
    public long SourceSize { get; set; }

    /// <summary>
    /// Gets or sets when the source file was last written, in UTC ticks, when it was made.
    /// </summary>
    public long SourceModifiedTicks { get; set; }

    /// <summary>
    /// Gets or sets the size of the optimized file.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets when it was made.
    /// </summary>
    public DateTime Created { get; set; }

    /// <summary>
    /// Gets or sets the streams in the optimized file.
    /// </summary>
    public IReadOnlyList<MediaStream> MediaStreams { get; set; } = [];
}
