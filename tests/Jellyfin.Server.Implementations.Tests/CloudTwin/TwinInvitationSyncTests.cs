using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.CloudTwin;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.CloudTwin;

public sealed class TwinInvitationSyncTests
{
    private const string Key = "state/invitations.json";
    private const string FirstId = "0123456789abcdef0123456789abcdef";

    private static readonly string _hash = "$PBKDF2-SHA512$iterations=210000$" + new string('A', 32) + "$" + new string('B', 128);

    private readonly List<User> _homeUsers = [];
    private readonly Guid _publishedLibrary = Guid.NewGuid();
    private readonly MemoryStore _store = new();
    private readonly TwinInvitationSync _sync;

    public TwinInvitationSyncTests()
    {
        _homeUsers.Add(NewUser("alice"));

        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUsers()).Returns(() => _homeUsers.ToList());
        users.Setup(u => u.GetUserByName(It.IsAny<string>()))
            .Returns((string name) => _homeUsers.FirstOrDefault(u => string.Equals(u.Username, name, StringComparison.OrdinalIgnoreCase)));
        users.Setup(u => u.CreateUserAsync(It.IsAny<string>())).ReturnsAsync((string name) =>
        {
            if (name.Contains('/', StringComparison.Ordinal))
            {
                throw new ArgumentException("Invalid username", nameof(name));
            }

            var user = NewUser(name);
            _homeUsers.Add(user);
            return user;
        });

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "TV", ItemId = _publishedLibrary.ToString("N") },
            new VirtualFolderInfo { Name = "Private", ItemId = Guid.NewGuid().ToString("N") }
        ]);

        _sync = new TwinInvitationSync(users.Object, library.Object, NullLogger<TwinInvitationSync>.Instance);
    }

    [Fact]
    public async Task Sync_PersonWaiting_GetsARestrictedAccountWithTheirPassword()
    {
        SetTwin(new TwinInvitedUser { Id = FirstId, Name = "Dana", Password = _hash, InvitedBy = "alice" });
        _store.Set("state/" + FirstId + ".json", "{\"Items\":{\"x\":{\"Played\":true}}}");

        Assert.Equal(1, await Run());

        var dana = _homeUsers.Single(u => u.Username == "Dana");
        Assert.Equal(_hash, dana.Password);
        Assert.False(dana.HasPermission(PermissionKind.IsAdministrator));
        Assert.False(dana.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([_publishedLibrary], dana.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        var person = Twin().Users.Single();
        Assert.Equal(dana.Id.ToString("N"), person.HomeId);
        Assert.Null(person.Password);
        Assert.Null(person.Rejected);

        // The list of users is published with the new account, and what they watched moves to their new id.
        Assert.Contains("Dana", Text(TwinUsers.Key), StringComparison.Ordinal);
        Assert.False(_store.Objects.ContainsKey("state/" + FirstId + ".json"));
        Assert.Contains("\"Played\":true", Text("state/" + dana.Id.ToString("N") + ".json"), StringComparison.Ordinal);

        Assert.Equal(0, await Run());
        Assert.Equal(2, _homeUsers.Count);
    }

    [Fact]
    public async Task Sync_NameInUseOrDetailsUnusable_IsTurnedDownAndNoAccountIsMade()
    {
        SetTwin(
            new TwinInvitedUser { Id = FirstId, Name = "ALICE", Password = _hash },
            new TwinInvitedUser { Id = "1123456789abcdef0123456789abcdef", Name = "Erin", Password = "plain text" },
            new TwinInvitedUser { Id = "../../auth/users", Name = "Finn", Password = _hash },
            new TwinInvitedUser { Id = "2123456789abcdef0123456789abcdef", Name = "bad/name", Password = _hash },
            new TwinInvitedUser { Id = "3123456789abcdef0123456789abcdef", Name = " ", Password = _hash });

        Assert.Equal(0, await Run());

        Assert.Single(_homeUsers);
        Assert.All(Twin().Users, person =>
        {
            Assert.NotNull(person.Rejected);
            Assert.Null(person.Password);
            Assert.Null(person.HomeId);
        });
        Assert.False(_store.Objects.ContainsKey(TwinUsers.Key));
    }

    [Fact]
    public async Task Sync_TwinWritesInBetween_TheAccountIsMadeOnceAndBothChangesKeep()
    {
        SetTwin(new TwinInvitedUser { Id = FirstId, Name = "Dana", Password = _hash });
        _store.BeforeNextConditionalWrite = () =>
        {
            var data = Twin();
            data.Users.Add(new TwinInvitedUser { Id = "4123456789abcdef0123456789abcdef", Name = "Gil", Password = _hash });
            _store.Set(Key, JsonSerializer.Serialize(data, JsonDefaults.Options));
        };

        Assert.Equal(2, await Run());

        Assert.Equal(["alice", "Dana", "Gil"], _homeUsers.Select(u => u.Username));
        Assert.All(Twin().Users, person => Assert.NotNull(person.HomeId));
    }

    [Fact]
    public async Task Sync_NothingWaiting_TouchesNothing()
    {
        Assert.Equal(0, await Run());

        SetTwin(new TwinInvitedUser { Id = FirstId, Name = "Dana", HomeId = Guid.NewGuid().ToString("N") });
        Assert.Equal(0, await Run());
        Assert.Equal(0, _store.ConditionalWrites);
        Assert.Single(_homeUsers);
    }

    private static User NewUser(string name)
    {
        var user = new User(name, "auth", "reset") { Id = Guid.NewGuid() };
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        return user;
    }

    private Task<int> Run() => _sync.SyncAsync(_store, ["TV"], TestContext.Current.CancellationToken);

    private string Text(string key) => Encoding.UTF8.GetString(_store.Objects[key].Content);

    private TwinInvitations Twin() => JsonSerializer.Deserialize<TwinInvitations>(_store.Objects[Key].Content, JsonDefaults.Options)!;

    private void SetTwin(params TwinInvitedUser[] people)
    {
        var data = new TwinInvitations();
        data.Users.AddRange(people);
        _store.Set(Key, JsonSerializer.Serialize(data, JsonDefaults.Options));
    }

    private sealed class MemoryStore : IObjectStore
    {
        private int _version;

        public Dictionary<string, (byte[] Content, string Tag)> Objects { get; } = [];

        public int ConditionalWrites { get; private set; }

        public Action? BeforeNextConditionalWrite { get; set; }

        public void Set(string key, string content) => Objects[key] = (Encoding.UTF8.GetBytes(content), "v" + (++_version));

        public Task PutFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
        {
            Objects[key] = (content.ToArray(), "v" + (++_version));
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public Task<(byte[] Content, string Tag)?> GetAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult<(byte[] Content, string Tag)?>(Objects.TryGetValue(key, out var found) ? found : null);

        public Task<bool> PutIfUnchangedAsync(string key, ReadOnlyMemory<byte> content, string contentType, string? tag, CancellationToken cancellationToken)
        {
            if (key == Key)
            {
                var before = BeforeNextConditionalWrite;
                BeforeNextConditionalWrite = null;
                before?.Invoke();
            }

            var current = Objects.TryGetValue(key, out var found) ? found.Tag : null;
            if (!string.Equals(current, tag, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            Objects[key] = (content.ToArray(), "v" + (++_version));
            ConditionalWrites++;
            return Task.FromResult(true);
        }
    }
}
