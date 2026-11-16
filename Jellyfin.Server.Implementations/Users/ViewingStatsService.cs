using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// Builds viewing summaries from the stored play state of movies and episodes.
/// </summary>
public class ViewingStatsService : IViewingStatsService
{
    private const int TopCount = 5;

    private static readonly string _movieType = typeof(Movie).FullName!;
    private static readonly string _episodeType = typeof(Episode).FullName!;

    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="ViewingStatsService"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    public ViewingStatsService(IDbContextFactory<JellyfinDbContext> dbProvider)
    {
        _dbProvider = dbProvider;
    }

    /// <inheritdoc />
    public async Task<ViewingStats> GetYearAsync(Guid userId, int? year, CancellationToken cancellationToken)
    {
        var dbContext = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            // An item can hold several play-state rows; the latest play is the one that counts.
            var plays = (await dbContext.UserData
                    .Where(d => d.UserId.Equals(userId)
                        && d.Played
                        && d.LastPlayedDate != null
                        && d.Item != null
                        && (d.Item.Type == _movieType || d.Item.Type == _episodeType))
                    .Select(d => new
                    {
                        d.ItemId,
                        LastPlayed = d.LastPlayedDate!.Value,
                        d.Item!.Type,
                        d.Item.RunTimeTicks,
                        d.Item.SeriesId,
                        d.Item.SeriesName,
                        d.Item.Genres
                    })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .GroupBy(p => p.ItemId)
                .Select(g => g.MaxBy(p => p.LastPlayed)!)
                .ToList();

            var years = plays.Select(p => p.LastPlayed.Year).Distinct().OrderDescending().ToList();
            var chosen = year ?? (years.Count > 0 ? years[0] : DateTime.UtcNow.Year);
            var inYear = plays.Where(p => p.LastPlayed.Year == chosen).ToList();

            var episodes = inYear.Where(p => p.Type == _episodeType).ToList();
            var movies = inYear.Where(p => p.Type == _movieType).ToList();

            var topSeries = episodes
                .Where(p => p.SeriesId.HasValue)
                .GroupBy(p => p.SeriesId!.Value)
                .Select(g => new ViewingStatsEntry
                {
                    Id = g.Key,
                    Name = g.Select(p => p.SeriesName).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? string.Empty,
                    Count = g.Count()
                })
                .OrderByDescending(e => e.Count)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(TopCount)
                .ToList();

            // Episodes rarely carry genres of their own, so a series is counted by its own.
            var seriesIds = episodes.Where(p => p.SeriesId.HasValue).Select(p => p.SeriesId!.Value).Distinct().ToList();
            var seriesGenres = seriesIds.Count == 0
                ? []
                : await dbContext.BaseItems
                    .Where(i => seriesIds.Contains(i.Id))
                    .Select(i => i.Genres)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

            var topGenres = movies.Select(p => p.Genres)
                .Concat(seriesGenres)
                .SelectMany(g => (g ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ViewingStatsEntry { Name = g.Key, Count = g.Count() })
                .OrderByDescending(e => e.Count)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(TopCount)
                .ToList();

            return new ViewingStats
            {
                Year = chosen,
                Years = years,
                Movies = movies.Count,
                Episodes = episodes.Count,
                WatchTimeTicks = inYear.Sum(p => p.RunTimeTicks ?? 0),
                BusiestMonth = inYear.Count == 0
                    ? null
                    : inYear.GroupBy(p => p.LastPlayed.Month).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key,
                TopSeries = topSeries,
                TopGenres = topGenres
            };
        }
    }
}
