using System;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library.OptimizedVersions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.OptimizedVersions;

public class OptimizedVersionQueueResumerTests
{
    [Fact]
    public async Task ResumeWhenStarted_WaitsUntilTheServerHasFinishedStarting()
    {
        var started = false;
        var manager = new Mock<IOptimizedVersionManager>();
        manager.Setup(m => m.Resume()).Returns(() => started ? 2 : throw new InvalidOperationException("resumed too early"));
        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(a => a.CoreStartupHasCompleted).Returns(() => started);

        var resumer = new OptimizedVersionQueueResumer(
            manager.Object,
            appHost.Object,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<OptimizedVersionQueueResumer>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(10)
        };

        var resuming = resumer.ResumeWhenStartedAsync(TestContext.Current.CancellationToken);
        await Task.Delay(60, TestContext.Current.CancellationToken);
        manager.Verify(m => m.Resume(), Times.Never);

        started = true;

        Assert.Equal(2, await resuming.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResumeWhenStarted_ServerStopsFirst_ResumesNothing()
    {
        var manager = new Mock<IOptimizedVersionManager>();
        using var stopping = new CancellationTokenSource();
        var resumer = new OptimizedVersionQueueResumer(
            manager.Object,
            Mock.Of<IServerApplicationHost>(),
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<OptimizedVersionQueueResumer>.Instance)
        {
            PollInterval = TimeSpan.FromMilliseconds(10)
        };

        var resuming = resumer.ResumeWhenStartedAsync(stopping.Token);
        await stopping.CancelAsync();

        Assert.Equal(0, await resuming.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        manager.Verify(m => m.Resume(), Times.Never);
    }
}
