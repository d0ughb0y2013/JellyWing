using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Keeps play state and watchlists the same at home and on the twin, in both directions.
/// </summary>
/// <remarks>
/// For each user the twin holds one file. Home remembers what that file and its own data
/// looked like after the last sync; whichever side differs from that has changed. When both
/// have, the later one wins.
/// </remarks>
public class TwinPlayStateSync
{
    private const int Attempts = 4;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IWatchlistManager _watchlistManager;
    private readonly ILogger<TwinPlayStateSync> _logger;
    private readonly string _folder;

    /// <summary>
    /// Initializes a new instance of the <see cref="TwinPlayStateSync"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="watchlistManager">The watchlist manager.</param>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public TwinPlayStateSync(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IWatchlistManager watchlistManager,
        IApplicationPaths applicationPaths,
        ILogger<TwinPlayStateSync> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);

        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _watchlistManager = watchlistManager;
        _logger = logger;
        _folder = Path.Combine(applicationPaths.DataPath, "cloud-twin");
    }

    /// <summary>
    /// Syncs every enabled user's play state and watchlist for the published items.
    /// </summary>
    /// <param name="store">The twin's store.</param>
    /// <param name="target">The store's name, as used when publishing.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many changes went to the twin and how many came home.</returns>
    public async Task<(int ToTwin, int ToHome)> SyncAsync(IObjectStore store, string target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        var items = GetPublishedItems(target);
        if (items.Count == 0)
        {
            return (0, 0);
        }

        var toTwin = 0;
        var toHome = 0;
        foreach (var user in _userManager.GetUsers().Where(u => !u.HasPermission(PermissionKind.IsDisabled)).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (sent, received) = await SyncUserAsync(store, target, user, items, cancellationToken).ConfigureAwait(false);
            toTwin += sent;
            toHome += received;
        }

        if (toTwin > 0 || toHome > 0)
        {
            _logger.LogInformation("Cloud twin play state: {ToTwin} changes sent, {ToHome} brought home.", toTwin, toHome);
        }

        return (toTwin, toHome);
    }

    private static string Name(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static TwinPlayEntry FromHome(UserItemData? data)
        => new()
        {
            Played = data?.Played ?? false,
            PositionTicks = data?.PlaybackPositionTicks ?? 0,
            PlayCount = data?.PlayCount ?? 0,
            IsFavorite = data?.IsFavorite ?? false,
            LastPlayed = data?.LastPlayedDate
        };

    // Both sides changed since the last sync. Home only knows when something was last played, so
    // the twin's change stands unless home was played after it.
    private static bool TwinWins(TwinPlayEntry home, TwinPlayEntry twin)
        => home.LastPlayed is null || new DateTimeOffset(DateTime.SpecifyKind(home.LastPlayed.Value, DateTimeKind.Utc)).ToUnixTimeMilliseconds() <= twin.Changed;

    private async Task<(int ToTwin, int ToHome)> SyncUserAsync(IObjectStore store, string target, User user, List<BaseItem> items, CancellationToken cancellationToken)
    {
        var key = "state/" + Name(user.Id) + ".json";
        var before = LoadBase(target, user.Id);
        var toHome = 0;

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var found = await store.GetAsync(key, cancellationToken).ConfigureAwait(false);
            var remote = Parse(found?.Content) ?? new TwinUserState();
            var after = new TwinUserState { Target = target };
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var toTwin = 0;

            foreach (var item in items)
            {
                var id = Name(item.Id);
                var data = _userDataManager.GetUserData(user, item);
                var home = FromHome(data);
                before.Items.TryGetValue(id, out var last);
                remote.Items.TryGetValue(id, out var twin);

                var result = home;
                if (twin is not null && TwinPlayEntry.Same(home, twin))
                {
                    result = twin;
                }
                else if (twin is not null && !TwinPlayEntry.Same(twin, last) && (TwinPlayEntry.Same(home, last) || TwinWins(home, twin)))
                {
                    ApplyToHome(user, item, data, twin, cancellationToken);
                    toHome++;
                    result = twin;
                }
                else if (twin is not null || !home.IsBlank)
                {
                    home.Changed = now;
                    remote.Items[id] = home;
                    toTwin++;
                }

                if (!result.IsBlank)
                {
                    after.Items[id] = result;
                }
            }

            var (listSent, listReceived) = await SyncWatchlistAsync(user, items, before, remote, after, now, cancellationToken).ConfigureAwait(false);
            toTwin += listSent;
            toHome += listReceived;

            if (toTwin > 0)
            {
                remote.Target = null;
                var content = JsonSerializer.SerializeToUtf8Bytes(remote, JsonDefaults.Options);
                if (!await store.PutIfUnchangedAsync(key, content, "application/json", found?.Tag, cancellationToken).ConfigureAwait(false))
                {
                    // The twin wrote in between. What came home is already saved; go round again for the rest.
                    continue;
                }
            }

            SaveBase(user.Id, after);
            return (toTwin, toHome);
        }

        _logger.LogWarning("The cloud twin's play state for {User} kept changing; it will be synced on the next run.", user.Username);
        return (0, toHome);
    }

    private async Task<(int ToTwin, int ToHome)> SyncWatchlistAsync(
        User user,
        List<BaseItem> items,
        TwinUserState before,
        TwinUserState remote,
        TwinUserState after,
        long now,
        CancellationToken cancellationToken)
    {
        var toTwin = 0;
        var toHome = 0;
        var atHome = (await _watchlistManager.GetItemIdsAsync(user.Id, cancellationToken).ConfigureAwait(false)).ToHashSet();

        foreach (var item in items)
        {
            var id = Name(item.Id);
            var home = atHome.Contains(item.Id);
            var last = before.Watchlist.TryGetValue(id, out var lastEntry) && lastEntry.On;
            remote.Watchlist.TryGetValue(id, out var twin);

            if (twin is not null && twin.On != home)
            {
                if (twin.On != last)
                {
                    // The twin changed it; home follows.
                    if (twin.On)
                    {
                        await _watchlistManager.AddAsync(user.Id, item.Id, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await _watchlistManager.RemoveAsync(user.Id, item.Id, cancellationToken).ConfigureAwait(false);
                    }

                    home = twin.On;
                    toHome++;
                }
                else
                {
                    remote.Watchlist[id] = new TwinWatchlistEntry { On = home, Changed = now };
                    toTwin++;
                }
            }
            else if (twin is null && home)
            {
                remote.Watchlist[id] = new TwinWatchlistEntry { On = true, Changed = now };
                toTwin++;
            }

            if (home)
            {
                after.Watchlist[id] = new TwinWatchlistEntry { On = true };
            }
        }

        return (toTwin, toHome);
    }

    private void ApplyToHome(User user, BaseItem item, UserItemData? data, TwinPlayEntry twin, CancellationToken cancellationToken)
    {
        data ??= new UserItemData { Key = item.GetUserDataKeys()[0] };
        data.Played = twin.Played;
        data.PlaybackPositionTicks = twin.PositionTicks;
        data.IsFavorite = twin.IsFavorite;
        data.PlayCount = Math.Max(data.PlayCount, twin.PlayCount);
        if (twin.LastPlayed is not null && (data.LastPlayedDate is null || twin.LastPlayed > data.LastPlayedDate))
        {
            data.LastPlayedDate = twin.LastPlayed;
        }

        _userDataManager.SaveUserData(user, item, data, UserDataSaveReason.Import, cancellationToken);
    }

    // Only what the twin can show is synced: the items the publisher has sent to this store.
    private List<BaseItem> GetPublishedItems(string target)
    {
        var path = Path.Combine(_folder, "state.json");
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<TwinPublishState>(File.ReadAllBytes(path), JsonDefaults.Options) is { } published
                && string.Equals(published.Target, target, StringComparison.Ordinal))
            {
                return published.Items.Keys
                    .Select(id => _libraryManager.GetItemById(id))
                    .Where(item => item is not null)
                    .Select(item => item!)
                    .ToList();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The record of what was published could not be read; play state is not synced.");
        }

        return [];
    }

    private TwinUserState? Parse(byte[]? content)
    {
        if (content is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TwinUserState>(content, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "A play state file on the cloud twin could not be read and will be written again.");
            return null;
        }
    }

    private string BasePath(Guid userId) => Path.Combine(_folder, "playstate", Name(userId) + ".json");

    private TwinUserState LoadBase(string target, Guid userId)
    {
        var path = BasePath(userId);
        if (File.Exists(path)
            && Parse(File.ReadAllBytes(path)) is { } state
            && string.Equals(state.Target, target, StringComparison.Ordinal))
        {
            return state;
        }

        return new TwinUserState { Target = target };
    }

    private void SaveBase(Guid userId, TwinUserState state)
    {
        var path = BasePath(userId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options));
    }
}
