using System;
using System.Security.Claims;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

public class OptimizedVersionsControllerTests
{
    private readonly Mock<IOptimizedVersionManager> _manager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Path = "/media/Some \"Quoted\" Film (1999).mkv" };
    private readonly OptimizedVersionsController _subject;

    public OptimizedVersionsControllerTests()
    {
        _libraryManager.Setup(l => l.GetItemById<BaseItem>(_movie.Id, null)).Returns(_movie);
        _subject = new OptimizedVersionsController(_manager.Object, Mock.Of<IUserManager>(), _libraryManager.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) }
            }
        };
    }

    [Fact]
    public void GetOptimizedVersion_ReportsStateAndSize()
    {
        _manager.Setup(m => m.GetStatus(_movie)).Returns((OptimizedVersionState.Ready, 1234));

        var dto = Assert.IsType<OptimizedVersionStatusDto>(_subject.GetOptimizedVersion(_movie.Id).Value);

        Assert.Equal(OptimizedVersionState.Ready, dto.State);
        Assert.Equal(1234, dto.Size);
    }

    [Fact]
    public void GetOptimizedVersion_UnknownItem_IsNotFound()
    {
        Assert.IsType<NotFoundResult>(_subject.GetOptimizedVersion(Guid.NewGuid()).Result);
    }

    [Theory]
    [InlineData(true, typeof(NoContentResult))]
    [InlineData(false, typeof(NotFoundResult))]
    public void DeleteOptimizedVersion_ReportsWhetherACopyWasRemoved(bool removed, Type expected)
    {
        _manager.Setup(m => m.Delete(_movie)).Returns(removed);

        Assert.IsType(expected, _subject.DeleteOptimizedVersion(_movie.Id));
    }

    [Fact]
    public void GetOptimizedVersionSummary_ReportsCountAndSize()
    {
        _manager.Setup(m => m.GetSummary()).Returns((3, 9000));

        var dto = Assert.IsType<OptimizedVersionSummaryDto>(_subject.GetOptimizedVersionSummary().Value);

        Assert.Equal(3, dto.Count);
        Assert.Equal(9000, dto.TotalSize);
    }

    [Fact]
    public void DownloadOptimizedVersion_NoneReady_IsNotFound()
    {
        Assert.IsType<NotFoundResult>(_subject.DownloadOptimizedVersion(_movie.Id));
    }

    [Fact]
    public void DownloadOptimizedVersion_Ready_ServesTheCopyUnderTheOriginalNameAsMp4()
    {
        _manager.Setup(m => m.GetPath(_movie)).Returns("/data/optimized-versions/ab/abc.mp4");

        var file = Assert.IsType<PhysicalFileResult>(_subject.DownloadOptimizedVersion(_movie.Id));

        Assert.Equal("/data/optimized-versions/ab/abc.mp4", file.FileName);
        Assert.Equal("Some Quoted Film (1999).mp4", file.FileDownloadName);
        Assert.Equal("video/mp4", file.ContentType);
        Assert.True(file.EnableRangeProcessing);
    }
}
