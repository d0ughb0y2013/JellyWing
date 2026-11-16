using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Updates;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Updates;

public class RecommendedPluginInstallerTests
{
    private static readonly Guid _webhook = Guid.Parse("71552a5a-5c5c-4350-a2ae-ebe451a30173");
    private static readonly Guid _playbackReporting = Guid.Parse("5c534381-91a3-43cb-907a-35aa02eb9d2c");
    private static readonly Guid _chapterSegments = Guid.Parse("698b6f33-14ca-49b5-9d79-fc3c0ab941f5");

    private readonly Mock<IInstallationManager> _installationManager = new();
    private readonly Mock<IPluginManager> _pluginManager = new();
    private readonly List<Guid> _installed = [];

    public RecommendedPluginInstallerTests()
    {
        _installationManager
            .Setup(m => m.GetAvailablePackages(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _installationManager
            .Setup(m => m.InstallPackage(It.IsAny<InstallationInfo>(), It.IsAny<CancellationToken>()))
            .Callback<InstallationInfo, CancellationToken>((package, _) => _installed.Add(package.Id))
            .Returns(Task.CompletedTask);

        Offer(_webhook, "2.0.0.0", "1.0.0.0");
        Offer(_playbackReporting, "1.0.0.0");
        Offer(_chapterSegments, "1.0.0.0");
    }

    [Fact]
    public async Task InstallAsync_NothingInstalled_InstallsNewestCompatibleVersionOfEach()
    {
        InstallationInfo? webhook = null;
        _installationManager
            .Setup(m => m.InstallPackage(It.Is<InstallationInfo>(p => p.Id.Equals(_webhook)), It.IsAny<CancellationToken>()))
            .Callback<InstallationInfo, CancellationToken>((package, _) => webhook = package)
            .Returns(Task.CompletedTask);

        var count = await CreateInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Equal(new Version(2, 0, 0, 0), webhook?.Version);
    }

    [Fact]
    public async Task InstallAsync_PluginAlreadyInstalled_IsNotInstalledAgain()
    {
        _pluginManager
            .Setup(m => m.GetPlugin(_webhook, null))
            .Returns(new LocalPlugin("/plugins/webhook", true, new PluginManifest { Id = _webhook }));

        await CreateInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal([_playbackReporting, _chapterSegments], _installed);
    }

    [Fact]
    public async Task InstallAsync_NoCompatibleVersion_SkipsThatPlugin()
    {
        _installationManager
            .Setup(m => m.GetCompatibleVersions(It.IsAny<IEnumerable<PackageInfo>>(), null, _playbackReporting, null, null))
            .Returns([]);

        await CreateInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal([_webhook, _chapterSegments], _installed);
    }

    [Fact]
    public async Task InstallAsync_OneInstallFails_StillInstallsTheRest()
    {
        _installationManager
            .Setup(m => m.InstallPackage(It.Is<InstallationInfo>(p => p.Id.Equals(_webhook)), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("download failed"));

        var count = await CreateInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal([_playbackReporting, _chapterSegments], _installed);
    }

    [Fact]
    public async Task InstallAsync_RepositoryUnreachable_InstallsNothingAndDoesNotThrow()
    {
        _installationManager
            .Setup(m => m.GetAvailablePackages(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("offline"));

        var count = await CreateInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Empty(_installed);
    }

    [Fact]
    public void QueueInstall_TurnedOff_NeverAsksTheRepository()
    {
        CreateInstaller(enabled: false).QueueInstall();

        _installationManager.Verify(m => m.GetAvailablePackages(It.IsAny<CancellationToken>()), Times.Never);
    }

    private void Offer(Guid id, params string[] versionsNewestFirst)
    {
        var offered = new List<InstallationInfo>();
        foreach (var version in versionsNewestFirst)
        {
            offered.Add(new InstallationInfo { Id = id, Version = Version.Parse(version) });
        }

        _installationManager
            .Setup(m => m.GetCompatibleVersions(It.IsAny<IEnumerable<PackageInfo>>(), null, id, null, null))
            .Returns(offered);
    }

    private RecommendedPluginInstaller CreateInstaller(bool enabled = true)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [MediaBrowser.Controller.Extensions.ConfigurationExtensions.InstallRecommendedPluginsKey] = enabled.ToString()
            })
            .Build();

        return new RecommendedPluginInstaller(
            _installationManager.Object,
            _pluginManager.Object,
            configuration,
            Mock.Of<IHostApplicationLifetime>(),
            NullLogger<RecommendedPluginInstaller>.Instance);
    }
}
