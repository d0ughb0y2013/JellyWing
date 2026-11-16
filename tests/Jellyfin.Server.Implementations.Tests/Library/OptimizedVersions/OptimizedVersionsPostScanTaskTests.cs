using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.OptimizedVersions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.OptimizedVersions;

public class OptimizedVersionsPostScanTaskTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IOptimizedVersionManager> _manager = new();
    private readonly Movie _needsOne = new() { Id = Guid.NewGuid() };
    private readonly Movie _hasOne = new() { Id = Guid.NewGuid() };
    private readonly Movie _needsNone = new() { Id = Guid.NewGuid() };

    public OptimizedVersionsPostScanTaskTests()
    {
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([_needsOne, _hasOne, _needsNone]);
        _manager.Setup(m => m.GetStatus(_needsOne)).Returns((OptimizedVersionState.None, null));
        _manager.Setup(m => m.GetStatus(_hasOne)).Returns((OptimizedVersionState.Ready, 10));
        _manager.Setup(m => m.GetStatus(_needsNone)).Returns((OptimizedVersionState.NotNeeded, null));
        _manager.Setup(m => m.Request(It.IsAny<BaseItem>())).Returns((OptimizedVersionState.Queued, null));
    }

    [Fact]
    public async Task Run_TurnedOn_AsksOnlyForVideosThatNeedOneAndHaveNone()
    {
        await CreateTask(enabled: true).Run(new Progress<double>(), CancellationToken.None);

        _manager.Verify(m => m.Request(_needsOne), Times.Once);
        _manager.Verify(m => m.Request(It.IsAny<BaseItem>()), Times.Once);
    }

    [Fact]
    public async Task Run_LeftOff_AsksForNothing()
    {
        await CreateTask(enabled: null).Run(new Progress<double>(), CancellationToken.None);

        _manager.Verify(m => m.Request(It.IsAny<BaseItem>()), Times.Never);
        _libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    private OptimizedVersionsPostScanTask CreateTask(bool? enabled)
    {
        var settings = new Dictionary<string, string?>();
        if (enabled.HasValue)
        {
            settings[MediaBrowser.Controller.Extensions.ConfigurationExtensions.OptimizeNewVideosKey] = enabled.Value.ToString();
        }

        return new OptimizedVersionsPostScanTask(
            _libraryManager.Object,
            _manager.Object,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<OptimizedVersionsPostScanTask>.Instance);
    }
}
