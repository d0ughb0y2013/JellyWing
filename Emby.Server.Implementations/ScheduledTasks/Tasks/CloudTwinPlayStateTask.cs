using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.CloudTwin;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Configuration;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Keeps what was watched, favorites and watchlists the same at home and on the cloud twin.
/// </summary>
public class CloudTwinPlayStateTask : IScheduledTask
{
    private readonly TwinPlayStateSync _sync;
    private readonly TwinInvitationSync _invitations;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudTwinPlayStateTask"/> class.
    /// </summary>
    /// <param name="sync">The play state sync.</param>
    /// <param name="invitations">The invitation sync.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="configuration">The startup configuration.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CloudTwinPlayStateTask(
        TwinPlayStateSync sync,
        TwinInvitationSync invitations,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _sync = sync;
        _invitations = invitations;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Name => "Sync play state with cloud twin";

    /// <inheritdoc />
    public string Description => "Brings home what was watched on the cloud twin and sends it what was watched at home, along with favorites and watchlists, makes accounts for people invited on the twin, and refreshes who may sign in there and which libraries they see. Does nothing until the cloud twin is set up.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "CloudTwinPlayState";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var options = new TwinOptions(_configuration);
        if (!options.IsConfigured)
        {
            return;
        }

        var (store, target) = TwinStoreFactory.Create(options, _httpClientFactory.CreateClient(NamedClient.Default));

        // Accounts first, so someone who was just invited has their play state synced in the same run.
        if (options.AcceptInvitations)
        {
            await _invitations.SyncAsync(store, options.Libraries, options.IncludeLimitedUsers, cancellationToken).ConfigureAwait(false);
        }

        // Who may sign in is refreshed on every run, so someone removed, disabled or given a new
        // password at home is treated the same on the twin within one interval.
        var users = TwinUsers.Serialize(_userManager, _libraryManager, options.Libraries, options.IncludeLimitedUsers, out _);
        await store.PutBytesAsync(TwinUsers.Key, users, "application/json", cancellationToken).ConfigureAwait(false);

        if (options.SyncPlayState)
        {
            await _sync.SyncAsync(store, target, cancellationToken).ConfigureAwait(false);
        }
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
