using System;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data;
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
    /// Builds the list: every enabled user's id, name, password hash and whether they are an administrator.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <returns>The list as JSON.</returns>
    public static byte[] Serialize(IUserManager userManager)
    {
        ArgumentNullException.ThrowIfNull(userManager);

        // Password hashes, never passwords: the twin checks a sign-in the same way the server does.
        var users = userManager.GetUsers()
            .Where(u => !u.HasPermission(PermissionKind.IsDisabled))
            .OrderBy(u => u.Username, StringComparer.Ordinal)
            .Select(u => new { u.Id, Name = u.Username, u.Password, IsAdministrator = u.HasPermission(PermissionKind.IsAdministrator) });
        return JsonSerializer.SerializeToUtf8Bytes(users, JsonDefaults.Options);
    }
}
