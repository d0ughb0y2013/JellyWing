using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.CloudTwin;
using Emby.Server.Implementations.Library.OptimizedVersions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.CloudTwin;

public sealed class TwinPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jw-twin-" + Guid.NewGuid().ToString("N"));
    private readonly string _storeRoot;
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IOptimizedVersionManager> _optimizedVersions = new();
    private readonly Mock<IOptimizedVersionMaker> _maker = new();
    private readonly Guid _libraryId = Guid.NewGuid();
    private readonly List<BaseItem> _items = [];
    private readonly TwinPublisher _publisher;

    public TwinPublisherTests()
    {
        _storeRoot = Path.Combine(_root, "store");
        Directory.CreateDirectory(_storeRoot);

        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(_root, "data"));

        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Movies", ItemId = _libraryId.ToString("N") },
            new VirtualFolderInfo { Name = "Private", ItemId = Guid.NewGuid().ToString("N") }
        ]);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);
        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.AncestorIds.Length == 1 && q.AncestorIds[0].Equals(_libraryId))))
            .Returns(() => _items.ToArray());

        _publisher = new TwinPublisher(
            _libraryManager.Object,
            _optimizedVersions.Object,
            _maker.Object,
            paths.Object,
            NullLogger<TwinPublisher>.Instance);
    }

    [Fact]
    public async Task Publish_VideoThatPlaysAsIs_SendsItsFilePictureAndRecord()
    {
        var movie = AddMovie("Plays Anywhere", "movie.mp4", playsAsIs: true, image: "poster.jpg");

        var result = await PublishAsync();

        Assert.Equal((1, 0, 0, 0), result);
        var id = movie.Id.ToString("N");
        Assert.Equal("movie bytes", await File.ReadAllTextAsync(StorePath($"media/{id}.mp4"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(StorePath($"images/{id}.jpg")));

        var record = await ReadRecordAsync(movie.Id);
        Assert.Equal("Plays Anywhere", record.Name);
        Assert.Equal("Movie", record.Type);
        Assert.Equal("Movies", record.Library);
        Assert.Equal($"media/{id}.mp4", record.MediaKey);
        Assert.Equal("movie bytes".Length, record.MediaSize);
        Assert.True(File.Exists(StorePath("catalog/index.json")));
    }

    [Fact]
    public async Task Publish_VideoWithAnOptimizedVersion_SendsTheOptimizedFile()
    {
        var movie = AddMovie("Awkward", "awkward.mkv", playsAsIs: false);
        var optimized = Path.Combine(_root, "optimized.mp4");
        await File.WriteAllTextAsync(optimized, "optimized bytes", TestContext.Current.CancellationToken);
        _optimizedVersions.Setup(m => m.GetPath(movie)).Returns(optimized);

        await PublishAsync();

        Assert.Equal(
            "optimized bytes",
            await File.ReadAllTextAsync(StorePath($"media/{movie.Id:N}.mp4"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publish_VideoThatNeedsAnOptimizedVersion_AsksForOneAndWaits()
    {
        var movie = AddMovie("Awkward", "awkward.mkv", playsAsIs: false);

        var result = await PublishAsync();

        Assert.Equal((0, 0, 1, 0), result);
        _optimizedVersions.Verify(m => m.Request(movie), Times.Once);
        Assert.False(File.Exists(StorePath($"catalog/{movie.Id:N}.json")));
    }

    [Fact]
    public async Task Publish_NothingChanged_SendsNothingAgain()
    {
        var movie = AddMovie("Plays Anywhere", "movie.mp4", playsAsIs: true);
        await PublishAsync();
        File.Delete(StorePath($"media/{movie.Id:N}.mp4"));

        var result = await PublishAsync();

        Assert.Equal((0, 1, 0, 0), result);
        Assert.False(File.Exists(StorePath($"media/{movie.Id:N}.mp4")), "an unchanged file must not be sent again");
    }

    [Fact]
    public async Task Publish_FileReplaced_SendsItAgain()
    {
        var movie = AddMovie("Plays Anywhere", "movie.mp4", playsAsIs: true);
        await PublishAsync();
        await File.WriteAllTextAsync(movie.Path, "a new and longer cut", TestContext.Current.CancellationToken);

        var result = await PublishAsync();

        Assert.Equal((1, 0, 0, 0), result);
        Assert.Equal(
            "a new and longer cut",
            await File.ReadAllTextAsync(StorePath($"media/{movie.Id:N}.mp4"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publish_ItemGoneFromTheLibrary_RemovesItsFileAndRecord()
    {
        var movie = AddMovie("Short Lived", "movie.mp4", playsAsIs: true, image: "poster.jpg");
        await PublishAsync();
        _items.Clear();

        var result = await PublishAsync();

        Assert.Equal((0, 0, 0, 1), result);
        Assert.Empty(Directory.EnumerateFiles(_storeRoot, movie.Id.ToString("N") + ".*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Publish_EpisodeAndItsSeries_LinksThem()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "A Show" };
        var episode = new Episode { Id = Guid.NewGuid(), Name = "Pilot", SeriesId = series.Id, IndexNumber = 1, ParentIndexNumber = 1, Path = WriteFile("pilot.mp4", "pilot bytes") };
        _maker.Setup(m => m.Plan(episode)).Returns(Plan(playsAsIs: true));
        _items.AddRange([series, episode]);

        var result = await PublishAsync();

        Assert.Equal(2, result.Published);
        Assert.Equal("Series", (await ReadRecordAsync(series.Id)).Type);
        Assert.Null((await ReadRecordAsync(series.Id)).MediaKey);
        var record = await ReadRecordAsync(episode.Id);
        Assert.Equal(series.Id, record.SeriesId);
        Assert.Equal(1, record.IndexNumber);
    }

    [Fact]
    public async Task Publish_LibraryNotNamed_IsLeftAlone()
    {
        AddMovie("Plays Anywhere", "movie.mp4", playsAsIs: true);

        var result = await _publisher.PublishAsync(
            new FolderObjectStore(_storeRoot), "test", ["Private"], new Progress<double>(), CancellationToken.None);

        Assert.Equal((0, 0, 0, 0), result);
        Assert.Empty(Directory.EnumerateFiles(_storeRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FolderStore_KeyThatLeavesTheFolder_IsRefused()
    {
        var store = new FolderObjectStore(_storeRoot);

        Assert.Throws<ArgumentException>(() => store.DeleteAsync("../outside.txt", CancellationToken.None).GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private static OptimizedVersionPlan Plan(bool playsAsIs)
        => OptimizedVersionPlan.Create(
            playsAsIs ? "mp4" : "matroska",
            new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = playsAsIs ? "h264" : "hevc", Width = 1280, Height = 720, BitDepth = 8 },
            null);

    private Task<(int Published, int Unchanged, int Waiting, int Removed)> PublishAsync()
        => _publisher.PublishAsync(new FolderObjectStore(_storeRoot), "test", ["Movies"], new Progress<double>(), CancellationToken.None);

    private Movie AddMovie(string name, string file, bool playsAsIs, string? image = null)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = name, Path = WriteFile(file, "movie bytes") };
        if (image is not null)
        {
            movie.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = WriteFile(image, "picture") }];
        }

        _maker.Setup(m => m.Plan(movie)).Returns(Plan(playsAsIs));
        _items.Add(movie);
        return movie;
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string StorePath(string key) => Path.Combine(_storeRoot, key.Replace('/', Path.DirectorySeparatorChar));

    private async Task<TwinRecord> ReadRecordAsync(Guid id)
    {
        var json = await File.ReadAllBytesAsync(StorePath($"catalog/{id:N}.json"), TestContext.Current.CancellationToken);
        return JsonSerializer.Deserialize<TwinRecord>(json, Jellyfin.Extensions.Json.JsonDefaults.Options)!;
    }
}
