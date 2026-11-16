using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library.OptimizedVersions;

/// <summary>
/// Puts back in line the optimized versions that were waiting when the server last stopped.
/// </summary>
public sealed class OptimizedVersionQueueResumer : IHostedService
{
    private readonly IOptimizedVersionManager _manager;
    private readonly IServerApplicationHost _appHost;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<OptimizedVersionQueueResumer> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionQueueResumer"/> class.
    /// </summary>
    /// <param name="manager">The optimized version manager.</param>
    /// <param name="appHost">The application host.</param>
    /// <param name="lifetime">The application lifetime.</param>
    /// <param name="logger">The logger.</param>
    public OptimizedVersionQueueResumer(
        IOptimizedVersionManager manager,
        IServerApplicationHost appHost,
        IHostApplicationLifetime lifetime,
        ILogger<OptimizedVersionQueueResumer> logger)
    {
        _manager = manager;
        _appHost = appHost;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets how often to check whether the server has finished starting.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => ResumeWhenStartedAsync(_lifetime.ApplicationStopping), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Waits for the server to finish starting, then resumes what was waiting.
    /// Until then ffmpeg has not been located, so nothing could be made and what the
    /// encoder can do is not known.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of items put back in line.</returns>
    public async Task<int> ResumeWhenStartedAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!_appHost.CoreStartupHasCompleted)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return 0;
        }

        var resumed = _manager.Resume();
        if (resumed > 0)
        {
            _logger.LogInformation("Resuming {Count} optimized versions that were waiting.", resumed);
        }

        return resumed;
    }
}
