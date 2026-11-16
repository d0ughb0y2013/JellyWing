using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.ModelBinders;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The user watchlist controller.
/// </summary>
[Route("")]
[Authorize]
public class UserWatchlistController : BaseJellyfinApiController
{
    private readonly IWatchlistManager _watchlistManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserWatchlistController"/> class.
    /// </summary>
    /// <param name="watchlistManager">The watchlist manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="dtoService">The dto service.</param>
    public UserWatchlistController(
        IWatchlistManager watchlistManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IDtoService dtoService)
    {
        _watchlistManager = watchlistManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
    }

    /// <summary>
    /// Gets the items on a user's watchlist, most recently added first.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="startIndex">Optional. The index of the first item to return.</param>
    /// <param name="limit">Optional. The maximum number of items to return.</param>
    /// <param name="fields">Optional. Additional fields to return.</param>
    /// <param name="enableImages">Optional. Include image information in the output.</param>
    /// <param name="enableUserData">Optional. Include user data.</param>
    /// <param name="imageTypeLimit">Optional. The max number of images to return, per image type.</param>
    /// <param name="enableImageTypes">Optional. The image types to include in the output.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Watchlist returned.</response>
    /// <response code="404">User not found.</response>
    /// <returns>The watchlist items the user can access.</returns>
    [HttpGet("UserWatchlistItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Tags("UserData")]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetWatchlistItems(
        [FromQuery] Guid? userId,
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ItemFields[] fields,
        [FromQuery] bool? enableImages,
        [FromQuery] bool? enableUserData,
        [FromQuery] int? imageTypeLimit,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] ImageType[] enableImageTypes,
        CancellationToken cancellationToken)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        var ids = await _watchlistManager.GetItemIdsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return new QueryResult<BaseItemDto>(startIndex, 0, []);
        }

        // The query drops anything the user may not see; the watchlist supplies the order.
        var visible = _libraryManager
            .GetItemList(new InternalItemsQuery(user) { ItemIds = ids.ToArray() })
            .ToDictionary(item => item.Id);
        var ordered = new List<BaseItem>(visible.Count);
        foreach (var id in ids)
        {
            if (visible.TryGetValue(id, out var item))
            {
                ordered.Add(item);
            }
        }

        IEnumerable<BaseItem> page = ordered.Skip(startIndex ?? 0);
        if (limit.HasValue)
        {
            page = page.Take(limit.Value);
        }

        var dtoOptions = new DtoOptions { Fields = fields }
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);

        return new QueryResult<BaseItemDto>(
            startIndex,
            ordered.Count,
            _dtoService.GetBaseItemDtos(page.ToArray(), dtoOptions, user));
    }

    /// <summary>
    /// Adds an item to a user's watchlist.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="userId">User id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Item is on the watchlist.</response>
    /// <response code="404">User or item not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("UserWatchlistItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Tags("UserData")]
    public async Task<ActionResult> AddWatchlistItem(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null || _libraryManager.GetItemById<BaseItem>(itemId, user) is null)
        {
            return NotFound();
        }

        await _watchlistManager.AddAsync(user.Id, itemId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes an item from a user's watchlist.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <param name="userId">User id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Item is not on the watchlist.</response>
    /// <response code="404">User not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("UserWatchlistItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Tags("UserData")]
    public async Task<ActionResult> RemoveWatchlistItem(
        [FromRoute, Required] Guid itemId,
        [FromQuery] Guid? userId,
        CancellationToken cancellationToken)
    {
        userId = RequestHelpers.GetUserId(User, userId);
        var user = _userManager.GetUserById(userId.Value);
        if (user is null)
        {
            return NotFound();
        }

        await _watchlistManager.RemoveAsync(user.Id, itemId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
