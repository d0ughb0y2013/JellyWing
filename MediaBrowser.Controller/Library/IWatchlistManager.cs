using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Keeps each user's list of items they intend to watch.
/// </summary>
public interface IWatchlistManager
{
    /// <summary>
    /// Gets the ids of the items on a user's watchlist, most recently added first.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The item ids.</returns>
    Task<IReadOnlyList<Guid>> GetItemIdsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds an item to a user's watchlist.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> if the item was already on the watchlist.</returns>
    Task<bool> AddAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);

    /// <summary>
    /// Removes an item from a user's watchlist.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>false</c> if the item was not on the watchlist.</returns>
    Task<bool> RemoveAsync(Guid userId, Guid itemId, CancellationToken cancellationToken);
}
