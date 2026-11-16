using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Emby.Server.Implementations.CloudTwin;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.CloudTwin;

public sealed class TwinUsersTests
{
    private readonly Guid _tv = Guid.NewGuid();
    private readonly Guid _films = Guid.NewGuid();
    private readonly Guid _private = Guid.NewGuid();
    private readonly List<User> _users = [];
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();

    public TwinUsersTests()
    {
        _userManager.Setup(u => u.GetUsers()).Returns(() => _users.ToList());
        _libraryManager.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "TV", ItemId = _tv.ToString("N") },
            new VirtualFolderInfo { Name = "Films", ItemId = _films.ToString("N") },
            new VirtualFolderInfo { Name = "Private", ItemId = _private.ToString("N") }
        ]);
    }

    [Fact]
    public void Serialize_SpellsOutEachPersonsPublishedLibraries()
    {
        var everything = Add("everything");
        everything.SetPermission(PermissionKind.IsAdministrator, true);

        var some = Add("some");
        some.SetPermission(PermissionKind.EnableAllFolders, false);
        some.SetPreference(PreferenceKind.EnabledFolders, new[] { _films, _private });

        var none = Add("none");
        none.SetPermission(PermissionKind.EnableAllFolders, false);

        var listed = Build(out var leftOut);

        Assert.Empty(leftOut);
        Assert.Equal(["TV", "Films"], Libraries(listed, "everything"));
        Assert.True(listed.Single(u => u.GetProperty("Name").GetString() == "everything").GetProperty("IsAdministrator").GetBoolean());
        Assert.Equal(["Films"], Libraries(listed, "some"));
        Assert.Empty(Libraries(listed, "none"));
    }

    [Fact]
    public void Serialize_LeavesOutPeopleTheTwinCannotLimit()
    {
        Add("plain");
        Add("disabled").SetPermission(PermissionKind.IsDisabled, true);
        Add("home only").SetPermission(PermissionKind.EnableRemoteAccess, false);
        Add("rated").MaxParentalRatingScore = 10;
        Add("tagged").SetPreference(PreferenceKind.BlockedTags, new[] { "scary" });
        Add("allowed tags").SetPreference(PreferenceKind.AllowedTags, new[] { "kids" });
        Add("unrated blocked").SetPreference(PreferenceKind.BlockUnratedItems, new[] { "Movie" });
        Add("no playing").SetPermission(PermissionKind.EnableMediaPlayback, false);
        var scheduled = Add("scheduled");
        scheduled.AccessSchedules.Add(new AccessSchedule(Jellyfin.Database.Implementations.Enums.DynamicDayOfWeek.Everyday, 8, 20, scheduled.Id));

        var listed = Build(out var leftOut);

        Assert.Equal(["plain"], listed.Select(u => u.GetProperty("Name").GetString()));
        Assert.Equal(
            ["allowed tags", "home only", "no playing", "rated", "scheduled", "tagged", "unrated blocked"],
            leftOut.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Serialize_WhenToldTo_IncludesLimitedPeopleButNeverThoseKeptAtHome()
    {
        Add("rated").MaxParentalRatingScore = 10;
        Add("home only").SetPermission(PermissionKind.EnableRemoteAccess, false);

        var listed = Build(out var leftOut, includeLimited: true);

        Assert.Equal(["rated"], listed.Select(u => u.GetProperty("Name").GetString()));
        Assert.Equal(["home only"], leftOut);
    }

    private static string[] Libraries(List<JsonElement> listed, string name)
        => listed.Single(u => u.GetProperty("Name").GetString() == name).GetProperty("Libraries").EnumerateArray().Select(l => l.GetString()!).ToArray();

    private User Add(string name)
    {
        var user = new User(name, "auth", "reset") { Id = Guid.NewGuid() };
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        _users.Add(user);
        return user;
    }

    private List<JsonElement> Build(out IReadOnlyList<string> leftOut, bool includeLimited = false)
    {
        var json = TwinUsers.Serialize(_userManager.Object, _libraryManager.Object, ["TV", "Films"], includeLimited, out leftOut);
        return JsonDocument.Parse(json).RootElement.EnumerateArray().ToList();
    }
}
