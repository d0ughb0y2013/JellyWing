using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Common.Updates;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Extensions;
using MediaBrowser.Model.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Updates;

/// <summary>
/// Installs a fixed set of plugins from the configured repositories when a server is first set up.
/// </summary>
public class RecommendedPluginInstaller : IRecommendedPluginInstaller
{
    private static readonly (string Name, Guid Id)[] _recommendedPlugins =
    [
        ("Webhook", Guid.Parse("71552a5a-5c5c-4350-a2ae-ebe451a30173")),
        ("Playback Reporting", Guid.Parse("5c534381-91a3-43cb-907a-35aa02eb9d2c")),
        ("Chapter Segments Provider", Guid.Parse("698b6f33-14ca-49b5-9d79-fc3c0ab941f5")),
        ("Intro Skipper", Guid.Parse("c83d86bb-a1e0-4c35-a113-e2101cf4ee6b"))
    ];

    // Repositories beyond the stock one that a recommended plugin is published in.
    private static readonly (string Name, string Url)[] _recommendedRepositories =
    [
        ("Intro Skipper", "https://intro-skipper.org/manifest.json")
    ];

    private readonly IInstallationManager _installationManager;
    private readonly IPluginManager _pluginManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RecommendedPluginInstaller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecommendedPluginInstaller"/> class.
    /// </summary>
    /// <param name="installationManager">The installation manager.</param>
    /// <param name="pluginManager">The plugin manager.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    /// <param name="configuration">The startup configuration.</param>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public RecommendedPluginInstaller(
        IInstallationManager installationManager,
        IPluginManager pluginManager,
        IServerConfigurationManager serverConfigurationManager,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<RecommendedPluginInstaller> logger)
    {
        _installationManager = installationManager;
        _pluginManager = pluginManager;
        _serverConfigurationManager = serverConfigurationManager;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <inheritdoc />
    public void QueueInstall()
    {
        if (!_configuration.GetInstallRecommendedPlugins())
        {
            _logger.LogInformation("Recommended plugins are turned off, skipping.");
            return;
        }

        // Setup must not wait on, or fail with, the plugin repository.
        _ = Task.Run(() => InstallAsync(_lifetime.ApplicationStopping), CancellationToken.None);
    }

    /// <summary>
    /// Installs every recommended plugin that is not installed yet.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of plugins installed.</returns>
    public async Task<int> InstallAsync(CancellationToken cancellationToken)
    {
        var installed = 0;
        try
        {
            AddRecommendedRepositories();

            var packages = await _installationManager.GetAvailablePackages(cancellationToken).ConfigureAwait(false);
            foreach (var (name, id) in _recommendedPlugins)
            {
                if (_pluginManager.GetPlugin(id) is not null)
                {
                    continue;
                }

                InstallationInfo? package = null;
                foreach (var candidate in _installationManager.GetCompatibleVersions(packages, id: id))
                {
                    package = candidate;
                    break;
                }

                if (package is null)
                {
                    _logger.LogWarning("No version of recommended plugin {Name} fits this server, skipping.", name);
                    continue;
                }

                try
                {
                    await _installationManager.InstallPackage(package, cancellationToken).ConfigureAwait(false);
                    installed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Recommended plugin {Name} could not be installed.", name);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The recommended plugins could not be installed.");
        }

        if (installed > 0)
        {
            _logger.LogInformation("Installed {Count} recommended plugins. They load after a restart.", installed);
        }

        return installed;
    }

    private void AddRecommendedRepositories()
    {
        var configuration = _serverConfigurationManager.Configuration;
        var repositories = new List<RepositoryInfo>(configuration.PluginRepositories);
        foreach (var (name, url) in _recommendedRepositories)
        {
            if (!repositories.Exists(r => string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                repositories.Add(new RepositoryInfo { Name = name, Url = url, Enabled = true });
            }
        }

        if (repositories.Count != configuration.PluginRepositories.Length)
        {
            configuration.PluginRepositories = repositories.ToArray();
            _serverConfigurationManager.SaveConfiguration();
        }
    }
}
