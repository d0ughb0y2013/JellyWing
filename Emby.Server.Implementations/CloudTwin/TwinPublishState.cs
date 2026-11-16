using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Everything that has been published, and where to.
/// </summary>
public class TwinPublishState
{
    /// <summary>
    /// Gets or sets the store this describes. A different store starts from nothing.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash of the combined catalog that was last published.
    /// </summary>
    public string? CatalogHash { get; set; }

    /// <summary>
    /// Gets or sets what was published for each item.
    /// </summary>
    public Dictionary<Guid, TwinPublishedItem> Items { get; set; } = [];
}
