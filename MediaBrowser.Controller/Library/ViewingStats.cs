using System.Collections.Generic;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// What a user watched in a year. An item counts in the year it was last played, because
/// only the last play of each item is kept.
/// </summary>
public class ViewingStats
{
    /// <summary>
    /// Gets or sets the year summarized.
    /// </summary>
    public int Year { get; set; }

    /// <summary>
    /// Gets or sets the years in which the user watched anything, newest first.
    /// </summary>
    public IReadOnlyList<int> Years { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of movies watched.
    /// </summary>
    public int Movies { get; set; }

    /// <summary>
    /// Gets or sets the number of episodes watched.
    /// </summary>
    public int Episodes { get; set; }

    /// <summary>
    /// Gets or sets the combined running time of what was watched, in ticks.
    /// </summary>
    public long WatchTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the month, 1 to 12, in which the most was watched; <c>null</c> if nothing was.
    /// </summary>
    public int? BusiestMonth { get; set; }

    /// <summary>
    /// Gets or sets the series with the most episodes watched.
    /// </summary>
    public IReadOnlyList<ViewingStatsEntry> TopSeries { get; set; } = [];

    /// <summary>
    /// Gets or sets the genres watched most, counting each movie and each series once.
    /// </summary>
    public IReadOnlyList<ViewingStatsEntry> TopGenres { get; set; } = [];
}
