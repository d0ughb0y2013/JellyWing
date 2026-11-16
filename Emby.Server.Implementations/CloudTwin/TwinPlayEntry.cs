using System;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// What one user has done with one item, as kept in the twin's store.
/// </summary>
public class TwinPlayEntry
{
    /// <summary>
    /// Gets or sets a value indicating whether the item has been watched.
    /// </summary>
    public bool Played { get; set; }

    /// <summary>
    /// Gets or sets where playback stopped, in ticks.
    /// </summary>
    public long PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets how many times it was played.
    /// </summary>
    public int PlayCount { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is a favorite.
    /// </summary>
    public bool IsFavorite { get; set; }

    /// <summary>
    /// Gets or sets when it was last played.
    /// </summary>
    public DateTime? LastPlayed { get; set; }

    /// <summary>
    /// Gets or sets when the entry last changed, in milliseconds since 1970.
    /// </summary>
    public long Changed { get; set; }

    /// <summary>
    /// Gets a value indicating whether the entry says nothing has happened.
    /// </summary>
    internal bool IsBlank => !Played && PositionTicks == 0 && !IsFavorite;

    /// <summary>
    /// Compares what matters for syncing. No entry is the same as a blank one.
    /// </summary>
    /// <param name="a">One entry.</param>
    /// <param name="b">The other.</param>
    /// <returns>Whether they describe the same state.</returns>
    internal static bool Same(TwinPlayEntry? a, TwinPlayEntry? b)
        => (a?.Played ?? false) == (b?.Played ?? false)
            && (a?.PositionTicks ?? 0) == (b?.PositionTicks ?? 0)
            && (a?.IsFavorite ?? false) == (b?.IsFavorite ?? false);
}
