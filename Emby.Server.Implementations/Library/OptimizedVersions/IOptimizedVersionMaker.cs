using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Makes the optimized version of one item.
/// </summary>
public interface IOptimizedVersionMaker
{
    /// <summary>
    /// Makes an item's optimized version unless it has one or needs none.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> if one was made.</returns>
    Task<bool> MakeAsync(BaseItem item, CancellationToken cancellationToken);
}
