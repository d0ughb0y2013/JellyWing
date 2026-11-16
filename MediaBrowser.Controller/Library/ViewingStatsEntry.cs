using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// A named count in a viewing summary.
/// </summary>
public class ViewingStatsEntry
{
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the item the entry is about, when there is one.
    /// </summary>
    public Guid? Id { get; set; }

    /// <summary>
    /// Gets or sets the count.
    /// </summary>
    public int Count { get; set; }
}
