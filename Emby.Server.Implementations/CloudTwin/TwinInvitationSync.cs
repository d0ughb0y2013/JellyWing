using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Makes accounts at home for the people who accepted an invitation on the twin.
/// </summary>
/// <remarks>
/// The account gets the name and the password hash picked on the twin, is never an
/// administrator, and sees only the libraries that are published to the twin.
/// </remarks>
public partial class TwinInvitationSync
{
    private const string Key = "state/invitations.json";
    private const int Attempts = 4;

    // A twin that has been tampered with should not be able to fill the server with accounts.
    private const int MaxNewAccountsPerRun = 25;

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TwinInvitationSync> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TwinInvitationSync"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public TwinInvitationSync(IUserManager userManager, ILibraryManager libraryManager, ILogger<TwinInvitationSync> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Makes the accounts that are waiting, tells the twin, and publishes the new list of users.
    /// </summary>
    /// <param name="store">The twin's store.</param>
    /// <param name="libraries">The names of the published libraries, which is all a new account may see.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many accounts were made.</returns>
    public async Task<int> SyncAsync(IObjectStore store, IReadOnlyList<string> libraries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(libraries);

        // What was decided for each person, by the id the twin gave them, so a retry does not decide twice.
        var made = new Dictionary<string, string>(StringComparer.Ordinal);
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var found = await store.GetAsync(Key, cancellationToken).ConfigureAwait(false);
            var data = Parse(found?.Content);
            if (data is null)
            {
                return 0;
            }

            foreach (var person in data.Users.Where(u => u.HomeId is null && u.Rejected is null && !made.ContainsKey(u.Id) && !refused.ContainsKey(u.Id)).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (made.Count >= MaxNewAccountsPerRun)
                {
                    _logger.LogWarning("More than {Max} people are waiting for an account from the cloud twin; the rest wait for the next run.", MaxNewAccountsPerRun);
                    break;
                }

                var (homeId, reason) = await CreateAsync(store, person, libraries, cancellationToken).ConfigureAwait(false);
                if (homeId is not null)
                {
                    made[person.Id] = homeId;
                }
                else
                {
                    refused[person.Id] = reason!;
                }
            }

            if (made.Count == 0 && refused.Count == 0)
            {
                return 0;
            }

            if (made.Count > 0)
            {
                // Published before the twin is told, so there is never a moment when the person can sign in nowhere.
                await store.PutBytesAsync(TwinUsers.Key, TwinUsers.Serialize(_userManager), "application/json", cancellationToken).ConfigureAwait(false);
            }

            foreach (var person in data.Users)
            {
                if (made.TryGetValue(person.Id, out var homeId))
                {
                    person.HomeId = homeId;
                    person.Password = null;
                }
                else if (refused.TryGetValue(person.Id, out var reason))
                {
                    person.Rejected = reason;
                    person.Password = null;
                }
            }

            var content = JsonSerializer.SerializeToUtf8Bytes(data, JsonDefaults.Options);
            if (await store.PutIfUnchangedAsync(Key, content, "application/json", found?.Tag, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("Cloud twin invitations: {Made} accounts made, {Refused} turned down.", made.Count, refused.Count);
                return made.Count;
            }
        }

        _logger.LogWarning("The cloud twin's invitations kept changing; the twin will be told about {Made} new accounts on the next run.", made.Count);
        return made.Count;
    }

    [GeneratedRegex(@"^\$PBKDF2-SHA512\$iterations=[0-9]{4,7}\$[0-9A-Fa-f]{16,128}\$[0-9A-Fa-f]{128}$")]
    private static partial Regex PasswordHashRegex();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex FirstIdRegex();

    private async Task<(string? HomeId, string? Reason)> CreateAsync(IObjectStore store, TwinInvitedUser person, IReadOnlyList<string> libraries, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(person.Name) || person.Password is null || !PasswordHashRegex().IsMatch(person.Password) || !FirstIdRegex().IsMatch(person.Id))
        {
            return (null, "The account details from the twin were not usable.");
        }

        if (_userManager.GetUserByName(person.Name) is not null)
        {
            return (null, "That name is already in use at home.");
        }

        Jellyfin.Database.Implementations.Entities.User user;
        try
        {
            user = await _userManager.CreateUserAsync(person.Name).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "The home server would not make an account for an invited person.");
            return (null, "The home server did not accept that name.");
        }

        var folders = _libraryManager.GetVirtualFolders()
            .Where(f => libraries.Contains(f.Name, StringComparer.OrdinalIgnoreCase) && Guid.TryParse(f.ItemId, out _))
            .Select(f => Guid.Parse(f.ItemId))
            .ToArray();

        user.Password = person.Password;
        user.SetPermission(PermissionKind.IsAdministrator, false);
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        user.SetPreference(PreferenceKind.EnabledFolders, folders);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);

        var homeId = user.Id.ToString("N", CultureInfo.InvariantCulture);
        await MoveStateAsync(store, person.Id, homeId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Made an account for {Name}, invited on the cloud twin by {InvitedBy}.", person.Name, person.InvitedBy);
        return (homeId, null);
    }

    // What the person watched before the account existed is kept under the id the twin gave them.
    private async Task MoveStateAsync(IObjectStore store, string firstId, string homeId, CancellationToken cancellationToken)
    {
        var from = "state/" + firstId + ".json";
        var found = await store.GetAsync(from, cancellationToken).ConfigureAwait(false);
        if (found is null)
        {
            return;
        }

        if (!await store.PutIfUnchangedAsync("state/" + homeId + ".json", found.Value.Content, "application/json", null, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Play state from before an invited account was made could not be carried over.");
            return;
        }

        await store.DeleteAsync(from, cancellationToken).ConfigureAwait(false);
    }

    private TwinInvitations? Parse(byte[]? content)
    {
        if (content is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TwinInvitations>(content, JsonDefaults.Options);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "The cloud twin's invitations could not be read.");
            return null;
        }
    }
}
