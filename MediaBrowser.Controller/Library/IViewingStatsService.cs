using System;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Summarizes what a user watched, from the play state the server keeps for each item.
/// </summary>
public interface IViewingStatsService
{
    /// <summary>
    /// Summarizes what a user watched in a year.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="year">The year, or <c>null</c> for the most recent year with anything watched.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The summary.</returns>
    Task<ViewingStats> GetYearAsync(Guid userId, int? year, CancellationToken cancellationToken);
}
