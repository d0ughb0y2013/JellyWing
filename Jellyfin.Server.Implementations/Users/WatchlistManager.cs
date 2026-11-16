using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// Stores watchlists as per-user, per-item preference rows, which needs no table of its own.
/// Rows for a deleted item are removed along with the item.
/// </summary>
public class WatchlistManager : IWatchlistManager
{
    private const string Client = "jellywing";
    private const string Key = "watchlist";

    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistManager"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    public WatchlistManager(IDbContextFactory<JellyfinDbContext> dbProvider)
    {
        _dbProvider = dbProvider;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> GetItemIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // The value is the time added in a sortable format.
            return await Entries(dbContext, userId)
                .OrderByDescending(e => e.Value)
                .ThenByDescending(e => e.Id)
                .Select(e => e.ItemId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> AddAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            if (await Entries(dbContext, userId).AnyAsync(e => e.ItemId.Equals(itemId), cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var added = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            dbContext.CustomItemDisplayPreferences.Add(new CustomItemDisplayPreferences(userId, itemId, Client, Key, added));
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Lost a race with another add of the same item; the unique index kept one row.
                return false;
            }

            return true;
        }
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(Guid userId, Guid itemId, CancellationToken cancellationToken)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var removed = await Entries(dbContext, userId)
                .Where(e => e.ItemId.Equals(itemId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            return removed > 0;
        }
    }

    private static IQueryable<CustomItemDisplayPreferences> Entries(JellyfinDbContext dbContext, Guid userId)
        => dbContext.CustomItemDisplayPreferences
            .Where(e => e.UserId.Equals(userId) && e.Client == Client && e.Key == Key);
}
