using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.OptimizedVersions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.OptimizedVersions;

public sealed class OptimizedVersionManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jw-optimized-manager-" + Guid.NewGuid().ToString("N"));
    private readonly IMediaSourceManager _previousMediaSourceManager;
    private readonly Mock<IMediaSourceManager> _mediaSourceManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly FakeMaker _maker;
    private readonly OptimizedVersionStore _store;
    private readonly OptimizedVersionManager _manager;
    private readonly Movie _movie;

    public OptimizedVersionManagerTests()
    {
        Directory.CreateDirectory(_root);
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(Path.Combine(_root, "data"));
        _store = new OptimizedVersionStore(paths.Object);
        _maker = new FakeMaker(_store);

        var source = Path.Combine(_root, "movie.mkv");
        File.WriteAllText(source, "original");
        _movie = new Movie { Id = Guid.NewGuid(), Path = source, VideoType = VideoType.VideoFile, Container = "mkv" };
        _libraryManager.Setup(l => l.GetItemById(_movie.Id)).Returns(_movie);
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([]);

        // An HEVC file: players need it converted.
        StreamsAre(new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc", Width = 1920, Height = 1080 });
        _previousMediaSourceManager = BaseItem.MediaSourceManager;
        BaseItem.MediaSourceManager = _mediaSourceManager.Object;

        _manager = new OptimizedVersionManager(
            _libraryManager.Object,
            _store,
            _maker,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<OptimizedVersionManager>.Instance);
    }

    [Fact]
    public void GetStatus_NothingAskedFor_IsNone()
    {
        Assert.Equal((OptimizedVersionState.None, null), _manager.GetStatus(_movie));
    }

    [Fact]
    public void GetStatus_FileAlreadyPlaysEverywhere_IsNotNeeded()
    {
        _movie.Container = "mp4";
        StreamsAre(new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = 1280, Height = 720, BitDepth = 8 });

        Assert.Equal(OptimizedVersionState.NotNeeded, _manager.GetStatus(_movie).State);
        Assert.Equal(OptimizedVersionState.NotNeeded, _manager.Request(_movie).State);
        Assert.Equal(0, _maker.Calls);
    }

    [Fact]
    public void GetStatus_NotAVideoFile_IsNotNeeded()
    {
        Assert.Equal(OptimizedVersionState.NotNeeded, _manager.GetStatus(new Folder { Id = Guid.NewGuid() }).State);
    }

    [Fact]
    public async Task Request_IsQueuedThenRunningThenReady()
    {
        // The worker may pick the request up before the call returns.
        Assert.Contains(_manager.Request(_movie).State, new[] { OptimizedVersionState.Queued, OptimizedVersionState.Running });

        await _maker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(OptimizedVersionState.Running, _manager.GetStatus(_movie).State);
        Assert.Null(_manager.GetPath(_movie));

        _maker.Finish.SetResult(true);
        await WaitForAsync(OptimizedVersionState.Ready);

        Assert.Equal(_store.GetMediaPath(_movie.Id), _manager.GetPath(_movie));
        Assert.True(_manager.GetStatus(_movie).Size > 0);
    }

    [Fact]
    public async Task Request_AskedTwiceWhileWaiting_IsMadeOnce()
    {
        _manager.Request(_movie);
        _manager.Request(_movie);
        await _maker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _manager.Request(_movie);

        _maker.Finish.SetResult(true);
        await WaitForAsync(OptimizedVersionState.Ready);
        _manager.Request(_movie);

        Assert.Equal(1, _maker.Calls);
    }

    [Fact]
    public async Task Request_MakingFails_IsFailedAndCanBeAskedForAgain()
    {
        _manager.Request(_movie);
        await _maker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _maker.Finish.SetException(new InvalidOperationException("ffmpeg failed"));
        await WaitForAsync(OptimizedVersionState.Failed);

        _maker.Reset();
        Assert.Contains(_manager.Request(_movie).State, new[] { OptimizedVersionState.Queued, OptimizedVersionState.Running });
        await _maker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _maker.Finish.SetResult(true);
        await WaitForAsync(OptimizedVersionState.Ready);
    }

    [Fact]
    public async Task Delete_RemovesAReadyCopyAndTheItemCanBeAskedForAgain()
    {
        _manager.Request(_movie);
        await _maker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(_manager.Delete(_movie), "a copy being made must not be removed");

        _maker.Finish.SetResult(true);
        await WaitForAsync(OptimizedVersionState.Ready);
        Assert.Equal(1, _manager.GetSummary().Count);
        Assert.True(_manager.GetSummary().TotalSize > 0);

        Assert.True(_manager.Delete(_movie));

        Assert.Equal((OptimizedVersionState.None, null), _manager.GetStatus(_movie));
        Assert.Equal((0, 0), _manager.GetSummary());
        Assert.False(_manager.Delete(_movie));
    }

    [Fact]
    public async Task Request_ForAFolder_MakesACopyOfEveryVideoInIt()
    {
        var season = new Folder { Id = Guid.NewGuid() };
        var second = AnotherMovie();
        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.AncestorIds.Length == 1 && q.AncestorIds[0].Equals(season.Id))))
            .Returns([_movie, second]);

        Assert.Equal(OptimizedVersionState.None, _manager.GetStatus(season).State);

        _maker.Finish.SetResult(true);
        _manager.Request(season);
        await WaitForAsync(OptimizedVersionState.Ready, season);

        Assert.Equal(2, _maker.Calls);
        Assert.Equal(_manager.GetStatus(_movie).Size + _manager.GetStatus(second).Size, _manager.GetStatus(season).Size);
    }

    [Fact]
    public void GetStatus_FolderWhoseVideosAllPlayEverywhere_IsNotNeeded()
    {
        var season = new Folder { Id = Guid.NewGuid() };
        _movie.Container = "mp4";
        StreamsAre(new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = 1280, Height = 720, BitDepth = 8 });
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([_movie]);

        Assert.Equal(OptimizedVersionState.NotNeeded, _manager.GetStatus(season).State);
    }

    [Fact]
    public async Task Resume_PicksUpWhatWasWaitingWhenTheServerStopped()
    {
        // A request is waiting when the first server stops.
        _manager.Request(_movie);
        await _maker.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(File.Exists(_store.QueuePath));

        // A new server starts with nothing in memory.
        var maker = new FakeMaker(_store);
        maker.Finish.SetResult(true);
        var restarted = new OptimizedVersionManager(
            _libraryManager.Object,
            _store,
            maker,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<OptimizedVersionManager>.Instance);

        Assert.Equal(1, restarted.Resume());

        for (var i = 0; i < 200 && restarted.GetStatus(_movie).State != OptimizedVersionState.Ready; i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal(OptimizedVersionState.Ready, restarted.GetStatus(_movie).State);
        Assert.False(File.Exists(_store.QueuePath), "nothing is waiting any more");
    }

    [Fact]
    public void Resume_NothingWasWaiting_DoesNothing()
    {
        Assert.Equal(0, _manager.Resume());
        Assert.Equal(0, _maker.Calls);
    }

    public void Dispose()
    {
        BaseItem.MediaSourceManager = _previousMediaSourceManager;
        Directory.Delete(_root, true);
    }

    private void StreamsAre(params MediaStream[] streams)
        => _mediaSourceManager.Setup(m => m.GetMediaStreams(It.IsAny<MediaBrowser.Controller.Persistence.MediaStreamQuery>())).Returns(streams);

    private Movie AnotherMovie()
    {
        var source = Path.Combine(_root, "second.mkv");
        File.WriteAllText(source, "another original");
        var movie = new Movie { Id = Guid.NewGuid(), Path = source, VideoType = VideoType.VideoFile, Container = "mkv" };
        _libraryManager.Setup(l => l.GetItemById(movie.Id)).Returns(movie);
        return movie;
    }

    private async Task WaitForAsync(OptimizedVersionState expected, BaseItem? item = null)
    {
        item ??= _movie;
        for (var i = 0; i < 200; i++)
        {
            if (_manager.GetStatus(item).State == expected)
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.Equal(expected, _manager.GetStatus(item).State);
    }

    /// <summary>
    /// Stands in for ffmpeg: waits until the test lets it finish, then stores a version.
    /// </summary>
    private sealed class FakeMaker : IOptimizedVersionMaker
    {
        private readonly OptimizedVersionStore _store;

        public FakeMaker(OptimizedVersionStore store)
        {
            _store = store;
        }

        public int Calls { get; private set; }

        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Finish { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public OptimizedVersionPlan? Plan(BaseItem item) => OptimizedVersionSelector.Plan(item, canToneMap: false);

        public async Task<bool> MakeAsync(BaseItem item, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            await Finish.Task.ConfigureAwait(false);

            var working = _store.GetWorkingPath(item.Id);
            await File.WriteAllTextAsync(working, "optimized bytes", cancellationToken).ConfigureAwait(false);
            var source = new FileInfo(item.Path);
            _store.Save(item.Id, working, new OptimizedVersionInfo
            {
                SourcePath = item.Path,
                SourceSize = source.Length,
                SourceModifiedTicks = source.LastWriteTimeUtc.Ticks,
                Size = new FileInfo(working).Length,
                Created = DateTime.UtcNow
            });
            return true;
        }
    }
}
