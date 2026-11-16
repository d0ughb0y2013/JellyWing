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
    /// Gets where an item stands. A folder stands where the videos in it that need an
    /// optimized version stand: ready once all of them are.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The state, and the size of the optimized versions that exist.</returns>
    (OptimizedVersionState State, long? Size) GetStatus(BaseItem item);

    /// <summary>
    /// Asks for an optimized version of an item to be made in the background. For a folder,
    /// asks for one of every video in it. Does nothing for a video that has one, does not
    /// need one, or is already waiting.
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

    /// <summary>
    /// Removes an item's optimized version.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns><c>false</c> if the item had none, or one is being made right now.</returns>
    bool Delete(BaseItem item);

    /// <summary>
    /// Gets how many optimized versions exist and how much disk space they use.
    /// </summary>
    /// <returns>The number of copies and their total size in bytes.</returns>
    (int Count, long TotalSize) GetSummary();

    /// <summary>
    /// Puts back in line whatever was waiting when the server last stopped.
    /// </summary>
    /// <returns>The number of items put back in line.</returns>
    int Resume();
}
