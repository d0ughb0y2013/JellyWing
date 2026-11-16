using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// What the web client publisher has sent, so the next run only sends what changed.
/// </summary>
public class TwinWebClientState
{
    /// <summary>
    /// Gets or sets the store this state describes. A different store starts from nothing.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the size and modified time of each file sent, by its path under the web client folder.
    /// </summary>
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
}
