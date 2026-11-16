using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class WatchlistManagerTests : SqliteDbTestFixture
{
    private readonly WatchlistManager _watchlist;
    private readonly Guid _alice = Guid.Parse("a0000000-0000-0000-0000-000000000000");
    private readonly Guid _bob = Guid.Parse("b0000000-0000-0000-0000-000000000000");
    private readonly Guid _movie = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private readonly Guid _series = Guid.Parse("20000000-0000-0000-0000-000000000000");

    public WatchlistManagerTests()
    {
        _watchlist = new WatchlistManager(CreateDbContextFactory());
    }

    [Fact]
    public async Task Add_ListsItemsMostRecentlyAddedFirst()
    {
        Assert.True(await _watchlist.AddAsync(_alice, _movie, CancellationToken.None));
        Assert.True(await _watchlist.AddAsync(_alice, _series, CancellationToken.None));

        Assert.Equal([_series, _movie], await _watchlist.GetItemIdsAsync(_alice, CancellationToken.None));
    }

    [Fact]
    public async Task Add_ItemAlreadyListed_KeepsOneEntry()
    {
        await _watchlist.AddAsync(_alice, _movie, CancellationToken.None);

        Assert.False(await _watchlist.AddAsync(_alice, _movie, CancellationToken.None));
        Assert.Equal([_movie], await _watchlist.GetItemIdsAsync(_alice, CancellationToken.None));
    }

    [Fact]
    public async Task Watchlists_AreSeparatePerUser()
    {
        await _watchlist.AddAsync(_alice, _movie, CancellationToken.None);
        await _watchlist.AddAsync(_bob, _series, CancellationToken.None);

        await _watchlist.RemoveAsync(_bob, _movie, CancellationToken.None);

        Assert.Equal([_movie], await _watchlist.GetItemIdsAsync(_alice, CancellationToken.None));
        Assert.Equal([_series], await _watchlist.GetItemIdsAsync(_bob, CancellationToken.None));
    }

    [Fact]
    public async Task Remove_ReportsWhetherTheItemWasListed()
    {
        await _watchlist.AddAsync(_alice, _movie, CancellationToken.None);

        Assert.True(await _watchlist.RemoveAsync(_alice, _movie, CancellationToken.None));
        Assert.False(await _watchlist.RemoveAsync(_alice, _movie, CancellationToken.None));
        Assert.Empty(await _watchlist.GetItemIdsAsync(_alice, CancellationToken.None));
    }

    [Fact]
    public async Task Watchlist_LeavesOtherPreferenceRowsAlone()
    {
        SeedPreference(new CustomItemDisplayPreferences(_alice, _movie, "emby", "sortBy", "SortName"));
        await _watchlist.AddAsync(_alice, _movie, CancellationToken.None);

        await _watchlist.RemoveAsync(_alice, _movie, CancellationToken.None);

        Assert.Empty(await _watchlist.GetItemIdsAsync(_alice, CancellationToken.None));
        Assert.Equal("sortBy", Assert.Single(ReadPreferenceKeys()));
    }

    [Fact]
    public async Task DeletingAnItem_TakesItOffEveryWatchlist()
    {
        SeedItem(_movie);
        SeedItem(_series);
        await _watchlist.AddAsync(_alice, _movie, CancellationToken.None);
        await _watchlist.AddAsync(_alice, _series, CancellationToken.None);
        await _watchlist.AddAsync(_bob, _movie, CancellationToken.None);

        var persistence = new ItemPersistenceService(
            CreateDbContextFactory(),
            Mock.Of<IServerApplicationHost>(),
            NullLogger<ItemPersistenceService>.Instance);
        persistence.DeleteItem([_movie]);

        Assert.Equal([_series], await _watchlist.GetItemIdsAsync(_alice, CancellationToken.None));
        Assert.Empty(await _watchlist.GetItemIdsAsync(_bob, CancellationToken.None));
    }

    private void SeedPreference(CustomItemDisplayPreferences preference)
    {
        using var context = CreateDbContext();
        context.CustomItemDisplayPreferences.Add(preference);
        context.SaveChanges();
    }

    private string[] ReadPreferenceKeys()
    {
        using var context = CreateDbContext();
        return context.CustomItemDisplayPreferences.Select(e => e.Key).ToArray();
    }

    private void SeedItem(Guid id)
    {
        using var context = CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity { Id = id, Type = typeof(Book).FullName! });
        context.SaveChanges();
    }
}
