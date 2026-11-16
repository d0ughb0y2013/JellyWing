using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.OptimizedVersions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.OptimizedVersions;

public sealed class OptimizedVersionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jw-optimized-" + Guid.NewGuid().ToString("N"));
    private readonly OptimizedVersionStore _store;
    private readonly Guid _itemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _source;

    public OptimizedVersionStoreTests()
    {
        Directory.CreateDirectory(_root);
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(_root, "data"));
        _store = new OptimizedVersionStore(paths.Object);

        _source = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(_source, "original");
    }

    [Fact]
    public void Find_AfterSave_ReturnsWhatWasRecorded()
    {
        SaveVersion();

        var found = _store.Find(_itemId, _source);

        Assert.NotNull(found);
        Assert.Equal("h264", Assert.Single(found.MediaStreams).Codec);
        Assert.True(File.Exists(_store.GetMediaPath(_itemId)));
    }

    [Fact]
    public void Find_NothingSaved_ReturnsNull()
    {
        Assert.Null(_store.Find(_itemId, _source));
    }

    [Fact]
    public void Find_SourceFileReplaced_ReturnsNull()
    {
        SaveVersion();
        File.WriteAllText(_source, "a different, longer original");

        Assert.Null(_store.Find(_itemId, _source));
    }

    [Fact]
    public void Find_SourceFileMoved_ReturnsNull()
    {
        SaveVersion();
        var moved = Path.Combine(_root, "renamed.mkv");
        File.Move(_source, moved);

        Assert.Null(_store.Find(_itemId, moved));
    }

    [Fact]
    public void Find_InfoFileUnreadable_ReturnsNull()
    {
        SaveVersion();
        File.WriteAllText(Path.ChangeExtension(_store.GetMediaPath(_itemId), ".json"), "{ not json");

        Assert.Null(_store.Find(_itemId, _source));
    }

    [Fact]
    public void Find_OnlyThePartialFileExists_ReturnsNull()
    {
        File.WriteAllText(_store.GetWorkingPath(_itemId), "half written");

        Assert.Null(_store.Find(_itemId, _source));
    }

    [Fact]
    public void Delete_RemovesEveryFileOfTheItem()
    {
        SaveVersion();
        File.WriteAllText(_store.GetWorkingPath(_itemId), "leftover");

        _store.Delete(_itemId);

        Assert.Empty(_store.GetStoredItemIds());
        Assert.Null(_store.Find(_itemId, _source));
    }

    [Fact]
    public void GetStoredItemIds_ListsEachItemOnce()
    {
        SaveVersion();
        var other = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        File.WriteAllText(_store.GetWorkingPath(other), "half written");

        Assert.Equal(new[] { _itemId, other }.Order(), _store.GetStoredItemIds().Order());
    }

    [Fact]
    public void HasRoomFor_LeavesSpaceForTheRestOfTheServer()
    {
        Assert.True(_store.HasRoomFor(0) || new DriveInfo(_root).AvailableFreeSpace < 2L * 1024 * 1024 * 1024);
        Assert.False(_store.HasRoomFor(new DriveInfo(_root).AvailableFreeSpace));
    }

    [Fact]
    public void GetMediaSourceId_IsStableAndNotTheItemId()
    {
        var id = OptimizedVersion.GetMediaSourceId(_itemId);

        Assert.Equal(id, OptimizedVersion.GetMediaSourceId(_itemId));
        Assert.NotEqual(_itemId.ToString("N"), id);
        Assert.True(Guid.TryParseExact(id, "N", out _));
    }

    [Fact]
    public async Task Provider_ItemWithAVersion_OffersItAsAnExtraVersion()
    {
        SaveVersion();
        var movie = new Movie { Id = _itemId, Path = _source, VideoType = VideoType.VideoFile, RunTimeTicks = 1234 };

        var sources = await new OptimizedVersionMediaSourceProvider(_store).GetMediaSources(movie, CancellationToken.None);

        var source = Assert.Single(sources);
        Assert.Equal(OptimizedVersion.GetMediaSourceId(_itemId), source.Id);
        Assert.Equal(_store.GetMediaPath(_itemId), source.Path);
        Assert.Equal("mp4", source.Container);
        Assert.Equal(1234, source.RunTimeTicks);
        Assert.Equal("Optimized", source.Name);
    }

    [Fact]
    public async Task Provider_VersionIsStaleOrItemIsNotAVideo_OffersNothing()
    {
        SaveVersion();
        var provider = new OptimizedVersionMediaSourceProvider(_store);
        var folder = new Folder { Id = _itemId, Path = _source };
        Assert.Empty(await provider.GetMediaSources(folder, CancellationToken.None));

        await File.WriteAllTextAsync(_source, "replaced with a new file", TestContext.Current.CancellationToken);
        var movie = new Movie { Id = _itemId, Path = _source, VideoType = VideoType.VideoFile };
        Assert.Empty(await provider.GetMediaSources(movie, CancellationToken.None));
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private void SaveVersion()
    {
        var working = _store.GetWorkingPath(_itemId);
        File.WriteAllText(working, "optimized bytes");
        var source = new FileInfo(_source);
        _store.Save(_itemId, working, new OptimizedVersionInfo
        {
            SourcePath = _source,
            SourceSize = source.Length,
            SourceModifiedTicks = source.LastWriteTimeUtc.Ticks,
            Size = new FileInfo(working).Length,
            Created = DateTime.UtcNow,
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264", Index = 0 }]
        });
    }
}
