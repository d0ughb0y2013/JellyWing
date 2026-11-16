using System;
using System.Collections.Generic;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// One user's play state and watchlist, the content of <c>state/&lt;user id&gt;.json</c> in the twin's store.
/// </summary>
public class TwinUserState
{
    /// <summary>
    /// Gets or sets the store this copy was synced with. Only used in the copy kept at home.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the play state of each item, by item id.
    /// </summary>
    public Dictionary<string, TwinPlayEntry> Items { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the watchlist, by item id.
    /// </summary>
    public Dictionary<string, TwinWatchlistEntry> Watchlist { get; set; } = new(StringComparer.Ordinal);
}
