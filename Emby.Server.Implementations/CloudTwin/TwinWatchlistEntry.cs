namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Whether an item is on a user's watchlist, as kept in the twin's store.
/// </summary>
public class TwinWatchlistEntry
{
    /// <summary>
    /// Gets or sets a value indicating whether the item is on the watchlist.
    /// </summary>
    public bool On { get; set; }

    /// <summary>
    /// Gets or sets when that last changed, in milliseconds since 1970.
    /// </summary>
    public long Changed { get; set; }
}
