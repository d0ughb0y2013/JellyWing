using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Helpers;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The user viewing stats controller.
/// </summary>
[Route("")]
[Authorize]
public class UserStatsController : BaseJellyfinApiController
{
    private readonly IViewingStatsService _viewingStatsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserStatsController"/> class.
    /// </summary>
    /// <param name="viewingStatsService">The viewing stats service.</param>
    public UserStatsController(IViewingStatsService viewingStatsService)
    {
        _viewingStatsService = viewingStatsService;
    }

    /// <summary>
    /// Gets a summary of what a user watched in a year.
    /// </summary>
    /// <param name="userId">User id. Only an administrator may ask about another user.</param>
    /// <param name="year">Optional. The year; the most recent year with anything watched if omitted.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Summary returned.</response>
    /// <returns>The summary.</returns>
    [HttpGet("UserStats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [Tags("UserData")]
    public async Task<ActionResult<ViewingStats>> GetUserStats(
        [FromQuery] Guid? userId,
        [FromQuery] int? year,
        CancellationToken cancellationToken)
    {
        var id = RequestHelpers.GetUserId(User, userId);
        return await _viewingStatsService.GetYearAsync(id, year, cancellationToken).ConfigureAwait(false);
    }
}
