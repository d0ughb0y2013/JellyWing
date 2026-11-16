using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.CloudTwin;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Keeps what was watched, favorites and watchlists the same at home and on the cloud twin.
/// </summary>
public class CloudTwinPlayStateTask : IScheduledTask
{
    private readonly TwinPlayStateSync _sync;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudTwinPlayStateTask"/> class.
    /// </summary>
    /// <param name="sync">The play state sync.</param>
    /// <param name="configuration">The startup configuration.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CloudTwinPlayStateTask(TwinPlayStateSync sync, IConfiguration configuration, IHttpClientFactory httpClientFactory)
    {
        _sync = sync;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "Sync play state with cloud twin";

    /// <inheritdoc />
    public string Description => "Brings home what was watched on the cloud twin and sends it what was watched at home, along with favorites and watchlists. Does nothing until the cloud twin is set up.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "CloudTwinPlayState";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var options = new TwinOptions(_configuration);
        if (!options.IsConfigured || !options.SyncPlayState)
        {
            return;
        }

        var (store, target) = TwinStoreFactory.Create(options, _httpClientFactory.CreateClient(NamedClient.Default));
        await _sync.SyncAsync(store, target, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks
        };
    }
}
