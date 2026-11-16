using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Migrations.Routines;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

/// <summary>
/// Covers which ancestor rows the repair adds, and that it never adds one it cannot justify.
/// </summary>
public sealed class RebuildMissingAncestorIdsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    private readonly Guid _root = Guid.Parse("10000000-0000-0000-0000-000000000000");
    private readonly Guid _library = Guid.Parse("20000000-0000-0000-0000-000000000000");
    private readonly Guid _series = Guid.Parse("30000000-0000-0000-0000-000000000000");
    private readonly Guid _season = Guid.Parse("40000000-0000-0000-0000-000000000000");
    private readonly Guid _episode = Guid.Parse("50000000-0000-0000-0000-000000000000");

    public RebuildMissingAncestorIdsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    [Fact]
    public async Task Perform_EpisodeLostItsRows_GetsEveryAncestorOfItsSeason()
    {
        SeedShow();
        SeedAncestors(_series, _root, _library);
        SeedAncestors(_season, _series, _root, _library);

        await PerformAsync();

        Assert.Equal(
            new[] { _root, _library, _series, _season }.Order(),
            AncestorsOf(_episode));
    }

    [Fact]
    public async Task Perform_WholeBranchLostItsRows_IsRebuiltLevelByLevel()
    {
        SeedShow();
        SeedAncestors(_series, _root, _library);

        await PerformAsync();

        Assert.Equal(new[] { _root, _library, _series }.Order(), AncestorsOf(_season));
        Assert.Equal(new[] { _root, _library, _series, _season }.Order(), AncestorsOf(_episode));
    }

    [Fact]
    public async Task Perform_NothingMissing_ChangesNothing()
    {
        SeedShow();
        SeedAncestors(_series, _root, _library);
        SeedAncestors(_season, _series, _root, _library);
        SeedAncestors(_episode, _season, _series, _root, _library);
        var before = CountAncestorRows();

        await PerformAsync();
        await PerformAsync();

        Assert.Equal(before, CountAncestorRows());
    }

    [Fact]
    public async Task Perform_ParentRowIsGone_AddsNothingForTheOrphan()
    {
        SeedShow();
        var orphan = Guid.Parse("60000000-0000-0000-0000-000000000000");
        SeedOrphan(orphan);

        await PerformAsync();

        Assert.Empty(AncestorsOf(orphan));
    }

    [Fact]
    public async Task Perform_TypeWithoutAncestors_IsLeftAlone()
    {
        SeedShow();
        var genre = Guid.Parse("70000000-0000-0000-0000-000000000000");
        SeedItem(Item(genre, typeof(Genre), _series));

        await PerformAsync();

        Assert.Empty(AncestorsOf(genre));
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static BaseItemEntity Item(Guid id, Type type, Guid? parentId) => new()
    {
        Id = id,
        Type = type.FullName!,
        ParentId = parentId,
        IsFolder = type != typeof(Episode)
    };

    private void SeedShow()
    {
        using var context = CreateDbContext();
        context.BaseItems.AddRange(
            Item(_root, typeof(AggregateFolder), null),
            Item(_library, typeof(CollectionFolder), null),
            Item(_series, typeof(Series), _root),
            Item(_season, typeof(Season), _series),
            Item(_episode, typeof(Episode), _season));
        context.SaveChanges();
    }

    private void SeedItem(BaseItemEntity item)
    {
        using var context = CreateDbContext();
        context.BaseItems.Add(item);
        context.SaveChanges();
    }

    // The pre-12 schema had no foreign key on ParentId, so upgraded databases hold such rows.
    private void SeedOrphan(Guid id)
    {
        using var context = CreateDbContext();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        context.BaseItems.Add(Item(id, typeof(Episode), Guid.Parse("99999999-0000-0000-0000-000000000000")));
        context.SaveChanges();
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = ON");
    }

    private void SeedAncestors(Guid itemId, params Guid[] ancestorIds)
    {
        using var context = CreateDbContext();
        context.AncestorIds.AddRange(ancestorIds.Select(ancestorId => new AncestorId
        {
            ItemId = itemId,
            ParentItemId = ancestorId,
            Item = null!,
            ParentItem = null!
        }));
        context.SaveChanges();
    }

    private Guid[] AncestorsOf(Guid itemId)
    {
        using var context = CreateDbContext();
        return context.AncestorIds
            .Where(e => e.ItemId.Equals(itemId))
            .Select(e => e.ParentItemId)
            .AsEnumerable()
            .Order()
            .ToArray();
    }

    private int CountAncestorRows()
    {
        using var context = CreateDbContext();
        return context.AncestorIds.Count();
    }

    private Task PerformAsync()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        return new RebuildMissingAncestorIds(
                new StartupLogger<RebuildMissingAncestorIds>(NullLogger<RebuildMissingAncestorIds>.Instance),
                factory.Object)
            .PerformAsync(CancellationToken.None);
    }

    private JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(new Mock<IApplicationPaths>().Object, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
