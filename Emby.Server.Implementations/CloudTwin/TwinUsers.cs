using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// The list of people who may sign in to the twin, <c>auth/users.json</c>.
/// </summary>
public static class TwinUsers
{
    /// <summary>
    /// The key of the list in the store.
    /// </summary>
    public const string Key = "auth/users.json";

    /// <summary>
    /// Builds the list: each person's id, name, password hash, whether they are an administrator,
    /// and which of the published libraries they may see.
    /// </summary>
    /// <remarks>
    /// The twin can limit a person to libraries and nothing finer. Someone who is limited in another
    /// way at home, by rating, tag, schedule or a ban on playing, would get more on the twin than at
    /// home, so they are left out unless <paramref name="includeLimited"/> says otherwise. Someone
    /// who may not connect from outside the home is always left out.
    /// </remarks>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="libraries">The names of the published libraries.</param>
    /// <param name="includeLimited">Whether to list people whose limits the twin cannot apply.</param>
    /// <param name="leftOut">The names of the enabled users who were left out.</param>
    /// <returns>The list as JSON.</returns>
    public static byte[] Serialize(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IReadOnlyList<string> libraries,
        bool includeLimited,
        out IReadOnlyList<string> leftOut)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(libraries);

        var published = libraryManager.GetVirtualFolders()
            .Where(f => libraries.Contains(f.Name, StringComparer.OrdinalIgnoreCase) && Guid.TryParse(f.ItemId, out _))
            .Select(f => (f.Name, Id: Guid.Parse(f.ItemId)))
            .ToList();

        var enabled = userManager.GetUsers()
            .Where(u => !u.HasPermission(PermissionKind.IsDisabled))
            .OrderBy(u => u.Username, StringComparer.Ordinal)
            .ToList();
        var listed = enabled
            .Where(u => u.HasPermission(PermissionKind.EnableRemoteAccess) && (includeLimited || !HasLimitsTheTwinCannotApply(u)))
            .ToList();
        leftOut = enabled.Except(listed).Select(u => u.Username).ToList();

        // Password hashes, never passwords: the twin checks a sign-in the same way the server does.
        // The libraries are always spelled out, so a list that says nothing never means "everything".
        var users = listed.Select(u =>
        {
            var all = u.HasPermission(PermissionKind.EnableAllFolders);
            var allowed = u.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
            return new
            {
                u.Id,
                Name = u.Username,
                u.Password,
                IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator),
                Libraries = published.Where(l => all || allowed.Contains(l.Id)).Select(l => l.Name).ToArray()
            };
        });
        return JsonSerializer.SerializeToUtf8Bytes(users, JsonDefaults.Options);
    }

    private static bool HasLimitsTheTwinCannotApply(User user)
        => user.MaxParentalRatingScore.HasValue
            || user.AccessSchedules.Count > 0
            || !user.HasPermission(PermissionKind.EnableMediaPlayback)
            || user.GetPreference(PreferenceKind.BlockedMediaFolders).Length > 0
            || user.GetPreference(PreferenceKind.BlockedTags).Length > 0
            || user.GetPreference(PreferenceKind.AllowedTags).Length > 0
            || user.GetPreference(PreferenceKind.BlockUnratedItems).Length > 0;
}
