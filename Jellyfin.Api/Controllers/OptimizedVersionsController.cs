using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models;
using Jellyfin.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Lets a user get a copy of an item that plays on any device, made on request.
/// </summary>
[Route("")]
[Authorize]
public class OptimizedVersionsController : BaseJellyfinApiController
{
    private readonly IOptimizedVersionManager _optimizedVersionManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="OptimizedVersionsController"/> class.
    /// </summary>
    /// <param name="optimizedVersionManager">The optimized version manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public OptimizedVersionsController(
        IOptimizedVersionManager optimizedVersionManager,
        IUserManager userManager,
        ILibraryManager libraryManager)
    {
        _optimizedVersionManager = optimizedVersionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Gets whether an item has an optimized version.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">Status returned.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The status.</returns>
    [HttpGet("Items/{itemId}/OptimizedVersion")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OptimizedVersionStatusDto> GetOptimizedVersion([FromRoute, Required] Guid itemId)
    {
        var item = GetItem(itemId);
        return item is null ? NotFound() : ToDto(_optimizedVersionManager.GetStatus(item));
    }

    /// <summary>
    /// Asks for an optimized version of an item to be made.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">Request accepted, or nothing to do.</response>
    /// <response code="404">Item not found.</response>
    /// <returns>The status after the request.</returns>
    [HttpPost("Items/{itemId}/OptimizedVersion")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<OptimizedVersionStatusDto> RequestOptimizedVersion([FromRoute, Required] Guid itemId)
    {
        var item = GetItem(itemId);
        return item is null ? NotFound() : ToDto(_optimizedVersionManager.Request(item));
    }

    /// <summary>
    /// Downloads the optimized version of an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">Optimized version downloaded.</response>
    /// <response code="404">Item not found, or it has no optimized version.</response>
    /// <returns>A <see cref="FileResult"/> containing the optimized version.</returns>
    [HttpGet("Items/{itemId}/OptimizedVersion/Download")]
    [Authorize(Policy = Policies.Download)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesFile("video/mp4")]
    public ActionResult DownloadOptimizedVersion([FromRoute, Required] Guid itemId)
    {
        var item = GetItem(itemId);
        var path = item is null ? null : _optimizedVersionManager.GetPath(item);
        if (item is null || path is null)
        {
            return NotFound();
        }

        // Quotes are valid in file names on Linux but not in the header that carries this one.
        var filename = Path.GetFileNameWithoutExtension(item.Path).Replace("\"", string.Empty, StringComparison.Ordinal) + ".mp4";
        return PhysicalFile(path, "video/mp4", filename, true);
    }

    /// <summary>
    /// Removes the optimized version of an item.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">Optimized version removed.</response>
    /// <response code="404">Item not found, or it has no optimized version that can be removed now.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Items/{itemId}/OptimizedVersion")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteOptimizedVersion([FromRoute, Required] Guid itemId)
    {
        var item = GetItem(itemId);
        return item is not null && _optimizedVersionManager.Delete(item) ? NoContent() : NotFound();
    }

    /// <summary>
    /// Gets how many optimized versions exist and how much disk space they use.
    /// </summary>
    /// <response code="200">Summary returned.</response>
    /// <returns>The summary.</returns>
    [HttpGet("OptimizedVersions/Summary")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OptimizedVersionSummaryDto> GetOptimizedVersionSummary()
    {
        var (count, totalSize) = _optimizedVersionManager.GetSummary();
        return new OptimizedVersionSummaryDto { Count = count, TotalSize = totalSize };
    }

    private static OptimizedVersionStatusDto ToDto((OptimizedVersionState State, long? Size) status)
        => new() { State = status.State, Size = status.Size };

    private BaseItem? GetItem(Guid itemId)
    {
        var userId = User.GetUserId();
        var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
        return _libraryManager.GetItemById<BaseItem>(itemId, user);
    }
}
