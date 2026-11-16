using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Where an item stands with respect to having an optimized version.
/// </summary>
public enum OptimizedVersionState
{
    /// <summary>
    /// The item has none and none has been asked for.
    /// </summary>
    None,

    /// <summary>
    /// The item has none because it does not need one, or cannot have one.
    /// </summary>
    NotNeeded,

    /// <summary>
    /// One has been asked for and is waiting its turn.
    /// </summary>
    Queued,

    /// <summary>
    /// One is being made now.
    /// </summary>
    Running,

    /// <summary>
    /// The item has one.
    /// </summary>
    Ready,

    /// <summary>
    /// Making one was tried and did not work.
    /// </summary>
    Failed
}

/// <summary>
/// Makes optimized versions of single items on request and reports on them.
/// </summary>
public interface IOptimizedVersionManager
{
    /// <summary>
    /// Gets where an item stands.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The state, and the size of the optimized version when there is one.</returns>
    (OptimizedVersionState State, long? Size) GetStatus(BaseItem item);

    /// <summary>
    /// Asks for an optimized version of an item to be made in the background.
    /// Does nothing if the item has one, does not need one, or is already waiting.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Where the item stands after the request.</returns>
    (OptimizedVersionState State, long? Size) Request(BaseItem item);

    /// <summary>
    /// Gets the file of an item's optimized version.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The path, or <c>null</c> if the item has no usable optimized version.</returns>
    string? GetPath(BaseItem item);
}
