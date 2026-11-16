using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class ViewingStatsServiceTests : SqliteDbTestFixture
{
    private const long Hour = TimeSpan.TicksPerHour;

    private readonly ViewingStatsService _service;
    private readonly Guid _alice = Guid.Parse("a0000000-0000-0000-0000-000000000000");
    private readonly Guid _bob = Guid.Parse("b0000000-0000-0000-0000-000000000000");
    private readonly Guid _showA = Guid.Parse("5a000000-0000-0000-0000-000000000000");
    private readonly Guid _showB = Guid.Parse("5b000000-0000-0000-0000-000000000000");

    public ViewingStatsServiceTests()
    {
        _service = new ViewingStatsService(CreateDbContextFactory());

        using var context = CreateDbContext();
        context.BaseItems.AddRange(
            new BaseItemEntity { Id = _showA, Type = typeof(Series).FullName!, Name = "Show A", Genres = "Drama|Mystery" },
            new BaseItemEntity { Id = _showB, Type = typeof(Series).FullName!, Name = "Show B", Genres = "Comedy" });
        context.SaveChanges();
    }

    [Fact]
    public async Task GetYear_CountsMoviesEpisodesAndRunningTime()
    {
        Watched(_alice, Movie(2 * Hour, "Drama"), new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        Watched(_alice, Episode(_showA, "Show A", Hour), new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc));
        Watched(_alice, Episode(_showA, "Show A", Hour), new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc));

        var stats = await _service.GetYearAsync(_alice, 2026, CancellationToken.None);

        Assert.Equal(1, stats.Movies);
        Assert.Equal(2, stats.Episodes);
        Assert.Equal(4 * Hour, stats.WatchTimeTicks);
        Assert.Equal(3, stats.BusiestMonth);
    }

    [Fact]
    public async Task GetYear_RanksSeriesByEpisodesAndGenresByTitles()
    {
        var day = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        Watched(_alice, Episode(_showA, "Show A", Hour), day);
        Watched(_alice, Episode(_showB, "Show B", Hour), day);
        Watched(_alice, Episode(_showB, "Show B", Hour), day);
        Watched(_alice, Movie(Hour, "Drama|Thriller"), day);

        var stats = await _service.GetYearAsync(_alice, 2026, CancellationToken.None);

        Assert.Equal(new[] { ("Show B", 2), ("Show A", 1) }, stats.TopSeries.Select(s => (s.Name, s.Count)));
        Assert.Equal(_showB, stats.TopSeries[0].Id);

        // Drama: one movie and Show A. Every other genre appears once.
        Assert.Equal(("Drama", 2), (stats.TopGenres[0].Name, stats.TopGenres[0].Count));
        Assert.Equal(new[] { "Comedy", "Mystery", "Thriller" }, stats.TopGenres.Skip(1).Select(g => g.Name));
    }

    [Fact]
    public async Task GetYear_LeavesOutOtherYearsOtherUsersAndUnfinishedItems()
    {
        Watched(_alice, Movie(Hour, "Drama"), new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc));
        Watched(_bob, Movie(Hour, "Drama"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Watched(_alice, Movie(Hour, "Drama"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), played: false);
        Watched(_alice, Movie(Hour, "Drama"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var stats = await _service.GetYearAsync(_alice, 2026, CancellationToken.None);

        Assert.Equal(1, stats.Movies);
        Assert.Equal(new[] { 2026, 2025 }, stats.Years);
    }

    [Fact]
    public async Task GetYear_NoYearGiven_UsesTheMostRecentYearWithAnythingWatched()
    {
        Watched(_alice, Movie(Hour, "Drama"), new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var stats = await _service.GetYearAsync(_alice, null, CancellationToken.None);

        Assert.Equal(2024, stats.Year);
        Assert.Equal(1, stats.Movies);
    }

    [Fact]
    public async Task GetYear_ItemWithSeveralPlayStateRows_IsCountedOnce()
    {
        var movie = Movie(Hour, "Drama");
        Watched(_alice, movie, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), key: "first");
        Watched(_alice, movie, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), key: "second");

        var stats = await _service.GetYearAsync(_alice, 2026, CancellationToken.None);

        Assert.Equal(1, stats.Movies);
        Assert.Equal(Hour, stats.WatchTimeTicks);
        Assert.Equal(4, stats.BusiestMonth);
    }

    [Fact]
    public async Task GetYear_NothingWatched_IsAllZero()
    {
        var stats = await _service.GetYearAsync(_alice, 2026, CancellationToken.None);

        Assert.Equal(0, stats.Movies + stats.Episodes);
        Assert.Null(stats.BusiestMonth);
        Assert.Empty(stats.Years);
        Assert.Empty(stats.TopSeries);
    }

    private Guid Movie(long runTimeTicks, string genres) => Seed(new BaseItemEntity
    {
        Id = Guid.NewGuid(),
        Type = typeof(Movie).FullName!,
        RunTimeTicks = runTimeTicks,
        Genres = genres
    });

    private Guid Episode(Guid seriesId, string seriesName, long runTimeTicks) => Seed(new BaseItemEntity
    {
        Id = Guid.NewGuid(),
        Type = typeof(Episode).FullName!,
        RunTimeTicks = runTimeTicks,
        SeriesId = seriesId,
        SeriesName = seriesName
    });

    private Guid Seed(BaseItemEntity item)
    {
        using var context = CreateDbContext();
        context.BaseItems.Add(item);
        context.SaveChanges();
        return item.Id;
    }

    private void Watched(Guid userId, Guid itemId, DateTime when, bool played = true, string key = "key")
    {
        using var context = CreateDbContext();
        if (!context.Users.Any(u => u.Id.Equals(userId)))
        {
            context.Users.Add(new User(userId.ToString("N"), "auth", "reset") { Id = userId });
        }

        context.UserData.Add(new UserData
        {
            UserId = userId,
            ItemId = itemId,
            CustomDataKey = key,
            Played = played,
            LastPlayedDate = when,
            Item = null,
            User = null
        });
        context.SaveChanges();
    }
}
