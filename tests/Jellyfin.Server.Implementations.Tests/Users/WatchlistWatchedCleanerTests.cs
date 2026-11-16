using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

public class WatchlistWatchedCleanerTests
{
    private readonly Mock<IWatchlistManager> _watchlist = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly User _user;
    private readonly WatchlistWatchedCleaner _cleaner;
    private readonly Guid _series = Guid.NewGuid();
    private readonly Guid _season = Guid.NewGuid();

    public WatchlistWatchedCleanerTests()
    {
        _user = new User(
            "jellyfin",
            typeof(DefaultAuthenticationProvider).FullName!,
            typeof(DefaultPasswordResetProvider).FullName!);
        _userManager.Setup(m => m.GetUserById(_user.Id)).Returns(_user);

        _cleaner = new WatchlistWatchedCleaner(
            Mock.Of<IUserDataManager>(),
            _watchlist.Object,
            _libraryManager.Object,
            _userManager.Object,
            new ConfigurationBuilder().Build(),
            NullLogger<WatchlistWatchedCleaner>.Instance);
    }

    [Fact]
    public async Task Handle_ListedMovieWatched_IsRemoved()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        ListIs(movie.Id);

        var removed = await _cleaner.HandleAsync(Saved(movie, played: true), CancellationToken.None);

        Assert.Equal([movie.Id], removed);
        _watchlist.Verify(w => w.RemoveAsync(_user.Id, movie.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false, UserDataSaveReason.PlaybackFinished)]
    [InlineData(true, UserDataSaveReason.PlaybackProgress)]
    [InlineData(true, UserDataSaveReason.UpdateUserRating)]
    [InlineData(true, UserDataSaveReason.Import)]
    public async Task Handle_NotAChangeThatMarksItWatched_KeepsIt(bool played, UserDataSaveReason reason)
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        ListIs(movie.Id);

        var removed = await _cleaner.HandleAsync(Saved(movie, played, reason), CancellationToken.None);

        Assert.Empty(removed);
        _watchlist.Verify(w => w.RemoveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_LastEpisodeWatched_RemovesTheFinishedSeasonAndSeries()
    {
        ListIs(_series, _season);
        CountsAre((_season, 3, 3), (_series, 6, 6));

        var removed = await _cleaner.HandleAsync(Saved(Episode(), played: true), CancellationToken.None);

        Assert.Equal(new[] { _season, _series }, removed);
    }

    [Fact]
    public async Task Handle_EpisodeWatchedButSeriesNotFinished_KeepsTheSeries()
    {
        ListIs(_series, _season);
        CountsAre((_season, 3, 3), (_series, 3, 6));

        var removed = await _cleaner.HandleAsync(Saved(Episode(), played: true), CancellationToken.None);

        Assert.Equal([_season], removed);
        _watchlist.Verify(w => w.RemoveAsync(_user.Id, _series, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_SeriesNotOnTheWatchlist_DoesNotCountIt()
    {
        var episode = Episode();
        ListIs(episode.Id);

        var removed = await _cleaner.HandleAsync(Saved(episode, played: true), CancellationToken.None);

        Assert.Equal([episode.Id], removed);
        _libraryManager.Verify(l => l.GetPlayedAndTotalCountBatch(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task Handle_EmptyWatchlist_DoesNothing()
    {
        ListIs();

        Assert.Empty(await _cleaner.HandleAsync(Saved(new Movie { Id = Guid.NewGuid() }, played: true), CancellationToken.None));
    }

    private Episode Episode() => new() { Id = Guid.NewGuid(), SeasonId = _season, SeriesId = _series };

    private UserDataSaveEventArgs Saved(BaseItem item, bool played, UserDataSaveReason reason = UserDataSaveReason.PlaybackFinished)
        => new()
        {
            Item = item,
            UserId = _user.Id,
            SaveReason = reason,
            UserData = new UserItemData { Key = "key", Played = played }
        };

    private void ListIs(params Guid[] ids)
        => _watchlist.Setup(w => w.GetItemIdsAsync(_user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(ids);

    private void CountsAre(params (Guid Id, int Played, int Total)[] counts)
    {
        var result = new Dictionary<Guid, (int Played, int Total)>();
        foreach (var (id, played, total) in counts)
        {
            result[id] = (played, total);
        }

        _libraryManager
            .Setup(l => l.GetPlayedAndTotalCountBatch(It.IsAny<IReadOnlyList<Guid>>(), _user))
            .Returns(result);
    }
}
