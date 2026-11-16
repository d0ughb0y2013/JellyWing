using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;
using Book = MediaBrowser.Controller.Entities.Book;

namespace Jellyfin.Api.Tests.Controllers;

public class UserWatchlistControllerTests
{
    private readonly Mock<IWatchlistManager> _watchlistManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IDtoService> _dtoService = new();
    private readonly User _user;
    private readonly UserWatchlistController _subject;

    public UserWatchlistControllerTests()
    {
        _user = new User(
            "jellyfin",
            typeof(DefaultAuthenticationProvider).FullName!,
            typeof(DefaultPasswordResetProvider).FullName!);
        _userManager.Setup(m => m.GetUserById(_user.Id)).Returns(_user);

        // Echo the items back so a test can read the order and paging that reached the DTO layer.
        _dtoService
            .Setup(d => d.GetBaseItemDtos(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<DtoOptions>(), _user, null, false))
            .Returns<IReadOnlyList<BaseItem>, DtoOptions, User, BaseItem, bool>(
                (items, _, _, _, _) => items.Select(i => new BaseItemDto { Id = i.Id }).ToArray());

        _subject = new UserWatchlistController(
            _watchlistManager.Object,
            _userManager.Object,
            _libraryManager.Object,
            _dtoService.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(InternalClaimTypes.UserId, _user.Id.ToString("N"))],
                        "test"))
                }
            }
        };
    }

    [Fact]
    public async Task GetWatchlistItems_KeepsWatchlistOrderAndDropsItemsTheUserCannotSee()
    {
        var first = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var last = Guid.NewGuid();
        ListIs(first, hidden, last);

        // The library answers in its own order and without the item the user has no access to.
        LibraryReturns(last, first);

        var result = await GetItems(startIndex: null, limit: null);

        Assert.Equal(2, result.TotalRecordCount);
        Assert.Equal([first, last], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetWatchlistItems_PagesAfterFiltering()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        ListIs(ids);
        LibraryReturns(ids);

        var result = await GetItems(startIndex: 1, limit: 2);

        Assert.Equal(5, result.TotalRecordCount);
        Assert.Equal([ids[1], ids[2]], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetWatchlistItems_EmptyWatchlist_DoesNotQueryTheLibrary()
    {
        ListIs();

        var result = await GetItems(startIndex: null, limit: null);

        Assert.Empty(result.Items);
        _libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public async Task AddWatchlistItem_ItemTheUserCannotSee_ReturnsNotFoundAndAddsNothing()
    {
        var itemId = Guid.NewGuid();

        var result = await _subject.AddWatchlistItem(itemId, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        _watchlistManager.Verify(m => m.AddAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddWatchlistItem_VisibleItem_AddsItForTheCallingUser()
    {
        var itemId = Guid.NewGuid();
        _libraryManager.Setup(l => l.GetItemById<BaseItem>(itemId, _user)).Returns(new Book { Id = itemId });

        var result = await _subject.AddWatchlistItem(itemId, null, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _watchlistManager.Verify(m => m.AddAsync(_user.Id, itemId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveWatchlistItem_RemovesItForTheCallingUser()
    {
        var itemId = Guid.NewGuid();

        var result = await _subject.RemoveWatchlistItem(itemId, null, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        _watchlistManager.Verify(m => m.RemoveAsync(_user.Id, itemId, It.IsAny<CancellationToken>()), Times.Once);
    }

    private void ListIs(params Guid[] ids)
        => _watchlistManager
            .Setup(m => m.GetItemIdsAsync(_user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ids);

    private void LibraryReturns(params Guid[] ids)
        => _libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(ids.Select(id => (BaseItem)new Book { Id = id }).ToArray());

    private async Task<MediaBrowser.Model.Querying.QueryResult<BaseItemDto>> GetItems(int? startIndex, int? limit)
    {
        var response = await _subject.GetWatchlistItems(null, startIndex, limit, [], null, null, null, [], CancellationToken.None);
        return Assert.IsType<MediaBrowser.Model.Querying.QueryResult<BaseItemDto>>(response.Value);
    }
}
