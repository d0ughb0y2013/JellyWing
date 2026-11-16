namespace Jellyfin.Api.Models;

/// <summary>
/// How many optimized versions exist and how much disk space they use.
/// </summary>
public class OptimizedVersionSummaryDto
{
    /// <summary>
    /// Gets or sets the number of optimized versions.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets their total size in bytes.
    /// </summary>
    public long TotalSize { get; set; }
}
