using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.CloudTwin;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Publishes the chosen libraries to the cloud twin's store. It does nothing until the
/// store and the libraries are set, and it has no default schedule.
/// </summary>
public class CloudTwinPublishTask : IScheduledTask
{
    private readonly TwinPublisher _publisher;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CloudTwinPublishTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CloudTwinPublishTask"/> class.
    /// </summary>
    /// <param name="publisher">The publisher.</param>
    /// <param name="configuration">The startup configuration.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public CloudTwinPublishTask(
        TwinPublisher publisher,
        IConfiguration configuration,
        IHttpClientFactory httpClientFactory,
        ILogger<CloudTwinPublishTask> logger)
    {
        _publisher = publisher;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Publish to cloud twin";

    /// <inheritdoc />
    public string Description => "Sends the chosen libraries to the cloud twin's storage: one file per video that plays anywhere, its picture, and a record describing it. Only what changed is sent.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "CloudTwinPublish";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var options = new TwinOptions(_configuration);
        if (!options.IsConfigured)
        {
            _logger.LogInformation("The cloud twin is not set up: name the libraries to publish and where to publish them.");
            return;
        }

        if (!string.IsNullOrEmpty(options.Folder))
        {
            await _publisher.PublishAsync(new FolderObjectStore(options.Folder), "folder:" + options.Folder, options.Libraries, progress, cancellationToken).ConfigureAwait(false);
            return;
        }

        var store = new S3ObjectStore(_httpClientFactory.CreateClient(NamedClient.Default), options);
        await _publisher.PublishAsync(store, $"s3:{options.S3Endpoint}/{options.S3Bucket}", options.Libraries, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield break;
    }
}
