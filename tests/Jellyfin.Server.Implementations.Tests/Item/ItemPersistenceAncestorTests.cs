using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers when saving an item may delete the ancestor rows it already has.
/// </summary>
public sealed class ItemPersistenceAncestorTests : SqliteDbTestFixture
{
    private readonly ItemPersistenceService _service;
    private readonly ILibraryManager _previousLibraryManager;
    private readonly Mock<ILibraryManager> _libraryManager = new();

    private readonly Guid _series = Guid.Parse("30000000-0000-0000-0000-000000000000");
    private readonly Guid _season = Guid.Parse("40000000-0000-0000-0000-000000000000");
    private readonly Guid _episode = Guid.Parse("50000000-0000-0000-0000-000000000000");

    public ItemPersistenceAncestorTests()
    {
        _service = new ItemPersistenceService(
            CreateDbContextFactory(),
            Mock.Of<IServerApplicationHost>(),
            NullLogger<ItemPersistenceService>.Instance);

        _libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        _previousLibraryManager = BaseItem.LibraryManager;
        BaseItem.LibraryManager = _libraryManager.Object;

        using var context = CreateDbContext();
        context.BaseItems.AddRange(
            Entity(_series, null, isFolder: true),
            Entity(_season, _series, isFolder: true),
            Entity(_episode, _season, isFolder: false));
        context.AncestorIds.AddRange(Ancestor(_episode, _season), Ancestor(_episode, _series));
        context.SaveChanges();
    }

    [Fact]
    public void SaveItems_ParentCannotBeLoaded_KeepsStoredAncestors()
    {
        // GetItemById answers null for the season, so the walk up the chain finds nothing.
        _service.SaveItems([new Book { Id = _episode, ParentId = _season, SortName = "episode" }], CancellationToken.None);

        Assert.Equal(new[] { _series, _season }.Order(), AncestorsOf(_episode));
    }

    [Fact]
    public void SaveItems_ItemMovedToAnotherParent_ReplacesStoredAncestors()
    {
        var newParent = Guid.Parse("80000000-0000-0000-0000-000000000000");
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(Entity(newParent, null, isFolder: true));
            context.SaveChanges();
        }

        _libraryManager.Setup(l => l.GetItemById(newParent)).Returns(new Folder { Id = newParent });

        _service.SaveItems([new Book { Id = _episode, ParentId = newParent, SortName = "episode" }], CancellationToken.None);

        Assert.Equal([newParent], AncestorsOf(_episode));
    }

    protected override void Dispose(bool disposing)
    {
        BaseItem.LibraryManager = _previousLibraryManager;
        base.Dispose(disposing);
    }

    private static BaseItemEntity Entity(Guid id, Guid? parentId, bool isFolder) => new()
    {
        Id = id,
        Type = isFolder ? typeof(Folder).FullName! : typeof(Book).FullName!,
        ParentId = parentId,
        IsFolder = isFolder
    };

    private static AncestorId Ancestor(Guid itemId, Guid ancestorId) => new()
    {
        ItemId = itemId,
        ParentItemId = ancestorId,
        Item = null!,
        ParentItem = null!
    };

    private List<Guid> AncestorsOf(Guid itemId)
    {
        using var context = CreateDbContext();
        return context.AncestorIds
            .Where(e => e.ItemId.Equals(itemId))
            .Select(e => e.ParentItemId)
            .AsEnumerable()
            .Order()
            .ToList();
    }
}
