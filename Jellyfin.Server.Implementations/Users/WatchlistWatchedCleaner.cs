using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// Takes an item off a user's watchlist once the user has watched it. A season or series
/// on the watchlist goes when every episode in it has been watched.
/// </summary>
public sealed class WatchlistWatchedCleaner : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly IWatchlistManager _watchlistManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WatchlistWatchedCleaner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchlistWatchedCleaner"/> class.
    /// </summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="watchlistManager">The watchlist manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="configuration">The startup configuration.</param>
    /// <param name="logger">The logger.</param>
    public WatchlistWatchedCleaner(
        IUserDataManager userDataManager,
        IWatchlistManager watchlistManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IConfiguration configuration,
        ILogger<WatchlistWatchedCleaner> logger)
    {
        _userDataManager = userDataManager;
        _watchlistManager = watchlistManager;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _configuration = configuration;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configuration.GetWatchlistRemoveWatched())
        {
            _userDataManager.UserDataSaved += OnUserDataSaved;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes from the user's watchlist whatever the saved change finished.
    /// </summary>
    /// <param name="e">The saved user data change.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ids taken off the watchlist.</returns>
    public async Task<IReadOnlyList<Guid>> HandleAsync(UserDataSaveEventArgs e, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Only a change that marks the item watched counts, not progress or a rating.
        if (e.Item is null
            || e.UserData is not { Played: true }
            || e.SaveReason is not (UserDataSaveReason.PlaybackFinished or UserDataSaveReason.TogglePlayed or UserDataSaveReason.UpdateUserData))
        {
            return [];
        }

        var listed = await _watchlistManager.GetItemIdsAsync(e.UserId, cancellationToken).ConfigureAwait(false);
        if (listed.Count == 0)
        {
            return [];
        }

        var removed = new List<Guid>();
        if (listed.Contains(e.Item.Id))
        {
            removed.Add(e.Item.Id);
        }

        // Finishing an episode can finish the season and the series it belongs to.
        Guid[] parents = e.Item switch
        {
            Episode episode => [episode.SeasonId, episode.SeriesId],
            Season season => [season.SeriesId],
            _ => []
        };

        var listedParents = parents.Where(listed.Contains).Distinct().ToArray();
        if (listedParents.Length > 0 && _userManager.GetUserById(e.UserId) is { } user)
        {
            var counts = _libraryManager.GetPlayedAndTotalCountBatch(listedParents, user);
            foreach (var parent in listedParents)
            {
                if (counts.TryGetValue(parent, out var count) && count.Total > 0 && count.Played >= count.Total)
                {
                    removed.Add(parent);
                }
            }
        }

        foreach (var itemId in removed)
        {
            await _watchlistManager.RemoveAsync(e.UserId, itemId, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // The save that raised this must not wait on, or fail with, the watchlist.
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleAsync(e, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not update the watchlist after {Item} was watched", e.Item?.Name);
            }
        });
    }
}
