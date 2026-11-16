using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.CloudTwin;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.CloudTwin;

public sealed class TwinPlayStateSyncTests : IDisposable
{
    private const string Target = "test-store";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jw-twin-state-" + Guid.NewGuid().ToString("N"));
    private readonly User _user = new("alice", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Name = "A Film" };
    private readonly Movie _other = new() { Id = Guid.NewGuid(), Name = "Another" };
    private readonly Dictionary<Guid, UserItemData> _homeData = [];
    private readonly HashSet<Guid> _homeWatchlist = [];
    private readonly List<UserDataSaveReason> _saveReasons = [];
    private readonly MemoryStore _store = new();
    private readonly TwinPlayStateSync _sync;

    public TwinPlayStateSyncTests()
    {
        var dataPath = Path.Combine(_root, "data");
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(dataPath);

        var published = new TwinPublishState { Target = Target };
        published.Items[_movie.Id] = new TwinPublishedItem();
        published.Items[_other.Id] = new TwinPublishedItem();
        Directory.CreateDirectory(Path.Combine(dataPath, "cloud-twin"));
        File.WriteAllBytes(Path.Combine(dataPath, "cloud-twin", "state.json"), JsonSerializer.SerializeToUtf8Bytes(published, JsonDefaults.Options));

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById(_movie.Id)).Returns(_movie);
        library.Setup(l => l.GetItemById(_other.Id)).Returns(_other);

        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUsers()).Returns([_user]);

        var userData = new Mock<IUserDataManager>();
        userData
            .Setup(u => u.GetUserData(_user, It.IsAny<BaseItem>()))
            .Returns((User _, BaseItem item) => _homeData.TryGetValue(item.Id, out var data) ? data : new UserItemData { Key = "k" });
        userData
            .Setup(u => u.SaveUserData(_user, It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
            .Callback((User _, BaseItem item, UserItemData data, UserDataSaveReason reason, CancellationToken _) =>
            {
                _homeData[item.Id] = data;
                _saveReasons.Add(reason);
            });

        var watchlist = new Mock<IWatchlistManager>();
        watchlist.Setup(w => w.GetItemIdsAsync(_user.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => _homeWatchlist.ToList());
        watchlist.Setup(w => w.AddAsync(_user.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, Guid id, CancellationToken _) => _homeWatchlist.Add(id));
        watchlist.Setup(w => w.RemoveAsync(_user.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, Guid id, CancellationToken _) => _homeWatchlist.Remove(id));

        _sync = new TwinPlayStateSync(library.Object, users.Object, userData.Object, watchlist.Object, paths.Object, NullLogger<TwinPlayStateSync>.Instance);
    }

    private string Key => "state/" + _user.Id.ToString("N") + ".json";

    private static string Id(BaseItem item) => item.Id.ToString("N");

    [Fact]
    public async Task Sync_WatchedAtHome_ReachesTheTwinOnce()
    {
        _homeData[_movie.Id] = new UserItemData { Key = "k", Played = true, PlayCount = 2, LastPlayedDate = DateTime.UtcNow };

        Assert.Equal((1, 0), await Run());
        var entry = Twin().Items[Id(_movie)];
        Assert.True(entry.Played);
        Assert.Equal(2, entry.PlayCount);
        Assert.False(Twin().Items.ContainsKey(Id(_other)));

        Assert.Equal((0, 0), await Run());
        Assert.Equal(1, _store.Writes);
    }

    [Fact]
    public async Task Sync_WatchedOnTheTwin_ComesHome()
    {
        var when = DateTime.UtcNow.AddMinutes(-5);
        SetTwin(state => state.Items[Id(_movie)] = new TwinPlayEntry { PositionTicks = 1234, PlayCount = 1, IsFavorite = true, LastPlayed = when, Changed = 1 });

        Assert.Equal((0, 1), await Run());
        var home = _homeData[_movie.Id];
        Assert.Equal(1234, home.PlaybackPositionTicks);
        Assert.True(home.IsFavorite);
        Assert.Equal(1, home.PlayCount);
        Assert.Equal([UserDataSaveReason.Import], _saveReasons);

        Assert.Equal((0, 0), await Run());
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public async Task Sync_UnwatchedAtHomeAfterASync_TellsTheTwin()
    {
        _homeData[_movie.Id] = new UserItemData { Key = "k", Played = true };
        await Run();

        _homeData[_movie.Id] = new UserItemData { Key = "k" };

        Assert.Equal((1, 0), await Run());
        Assert.False(Twin().Items[Id(_movie)].Played);
    }

    [Fact]
    public async Task Sync_BothChanged_TheLaterOneWins()
    {
        _homeData[_movie.Id] = new UserItemData { Key = "k", PlaybackPositionTicks = 100 };
        _homeData[_other.Id] = new UserItemData { Key = "k", PlaybackPositionTicks = 100 };
        await Run();

        var now = DateTimeOffset.UtcNow;
        // The film: watched on the twin a minute ago, at home an hour ago.
        _homeData[_movie.Id] = new UserItemData { Key = "k", PlaybackPositionTicks = 200, LastPlayedDate = now.AddHours(-1).UtcDateTime };
        // The other: the twin's change is the old one.
        _homeData[_other.Id] = new UserItemData { Key = "k", PlaybackPositionTicks = 200, LastPlayedDate = now.UtcDateTime };
        SetTwin(state =>
        {
            state.Items[Id(_movie)] = new TwinPlayEntry { PositionTicks = 300, Changed = now.AddMinutes(-1).ToUnixTimeMilliseconds() };
            state.Items[Id(_other)] = new TwinPlayEntry { PositionTicks = 300, Changed = now.AddHours(-1).ToUnixTimeMilliseconds() };
        });

        Assert.Equal((1, 1), await Run());
        Assert.Equal(300, _homeData[_movie.Id].PlaybackPositionTicks);
        Assert.Equal(200, _homeData[_other.Id].PlaybackPositionTicks);
        Assert.Equal(200, Twin().Items[Id(_other)].PositionTicks);
    }

    [Fact]
    public async Task Sync_Watchlist_GoesBothWays()
    {
        _homeWatchlist.Add(_movie.Id);
        SetTwin(state => state.Watchlist[Id(_other)] = new TwinWatchlistEntry { On = true, Changed = 1 });

        Assert.Equal((1, 1), await Run());
        Assert.True(Twin().Watchlist[Id(_movie)].On);
        Assert.Contains(_other.Id, _homeWatchlist);

        // Taken off on the twin, and off at home: each follows the other.
        SetTwin(state => state.Watchlist[Id(_movie)] = new TwinWatchlistEntry { On = false, Changed = 2 });
        _homeWatchlist.Remove(_other.Id);

        Assert.Equal((1, 1), await Run());
        Assert.Empty(_homeWatchlist);
        Assert.False(Twin().Watchlist[Id(_other)].On);
        Assert.Equal((0, 0), await Run());
    }

    [Fact]
    public async Task Sync_TwinWritesInBetween_NothingIsLost()
    {
        _homeData[_movie.Id] = new UserItemData { Key = "k", Played = true };
        _store.BeforeNextWrite = () => SetTwin(state => state.Items[Id(_other)] = new TwinPlayEntry { IsFavorite = true, Changed = 5 });

        Assert.Equal((1, 1), await Run());
        Assert.True(Twin().Items[Id(_movie)].Played);
        Assert.True(Twin().Items[Id(_other)].IsFavorite);
        Assert.True(_homeData[_other.Id].IsFavorite);
    }

    [Fact]
    public async Task Sync_KeepsWhatItDoesNotKnow()
    {
        SetTwin(state => state.Items["ffffffffffffffffffffffffffffffff"] = new TwinPlayEntry { Played = true, Changed = 1 });
        _homeData[_movie.Id] = new UserItemData { Key = "k", Played = true };

        await Run();

        Assert.True(Twin().Items["ffffffffffffffffffffffffffffffff"].Played);
    }

    [Fact]
    public async Task Sync_AnotherStore_DoesNothing()
    {
        _homeData[_movie.Id] = new UserItemData { Key = "k", Played = true };

        Assert.Equal((0, 0), await _sync.SyncAsync(_store, "somewhere-else", TestContext.Current.CancellationToken));
        Assert.Equal(0, _store.Writes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private Task<(int ToTwin, int ToHome)> Run() => _sync.SyncAsync(_store, Target, TestContext.Current.CancellationToken);

    private TwinUserState Twin() => JsonSerializer.Deserialize<TwinUserState>(_store.Objects[Key].Content, JsonDefaults.Options)!;

    private void SetTwin(Action<TwinUserState> change)
    {
        var state = _store.Objects.ContainsKey(Key) ? Twin() : new TwinUserState();
        change(state);
        _store.Set(Key, JsonSerializer.SerializeToUtf8Bytes(state, JsonDefaults.Options));
    }

    private sealed class MemoryStore : IObjectStore
    {
        private int _version;

        public Dictionary<string, (byte[] Content, string Tag)> Objects { get; } = [];

        public int Writes { get; private set; }

        public Action? BeforeNextWrite { get; set; }

        public void Set(string key, byte[] content) => Objects[key] = (content, "v" + (++_version));

        public Task PutFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(byte[] Content, string Tag)?> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult<(byte[] Content, string Tag)?>(Objects.TryGetValue(key, out var found) ? found : null);

        public Task<bool> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string contentType, string? tag, CancellationToken cancellationToken)
        {
            var before = BeforeNextWrite;
            BeforeNextWrite = null;
            before?.Invoke();

            var current = Objects.TryGetValue(key, out var found) ? found.Tag : null;
            if (!string.Equals(current, tag, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Set(key, content.ToArray());
            Writes++;
            return Task.FromResult(true);
        }
    }
}
