using MediaBrowser.Controller.Library;

namespace Jellyfin.Api.Models;

/// <summary>
/// Where an item stands with respect to having an optimized version.
/// </summary>
public class OptimizedVersionStatusDto
{
    /// <summary>
    /// Gets or sets the state.
    /// </summary>
    public OptimizedVersionState State { get; set; }

    /// <summary>
    /// Gets or sets the size in bytes of the optimized version, when there is one.
    /// </summary>
    public long? Size { get; set; }
}
