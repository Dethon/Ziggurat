using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

public sealed class UserIdentityEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly UserIdentityStore _userIdentityStore;
    private readonly FakeConfigService _configService = new();
    private readonly FakeLocalStorageService _localStorage = new();
    private readonly RecordingLogger<UserIdentityEffect> _logger = new();
    private readonly UserIdentityEffect _effect;

    public UserIdentityEffectTests()
    {
        _userIdentityStore = new UserIdentityStore(_dispatcher);

        _effect = new UserIdentityEffect(_dispatcher, _configService, _localStorage, _logger);
    }

    [Fact]
    public async Task LoadUsersAsync_ConfigHasUsers_PublishesThemAndRestoresTheSavedChoice()
    {
        GivenUsers("alice", "bob");
        _localStorage.Seed("selectedUserId", "bob");

        await _effect.LoadUsersAsync();

        _userIdentityStore.State.AvailableUsers.Select(u => u.Id).ShouldBe(["alice", "bob"]);
        _userIdentityStore.State.SelectedUserId.ShouldBe("bob");
    }

    [Fact]
    public async Task LoadUsersAsync_SavedUserIsNotInTheList_SelectsNobody()
    {
        GivenUsers("alice");
        _localStorage.Seed("selectedUserId", "carol");

        await _effect.LoadUsersAsync();

        _userIdentityStore.State.SelectedUserId.ShouldBeNull();
    }

    [Fact]
    public async Task LoadUsersAsync_ConfigRequestFails_PublishesAnEmptyList()
    {
        _configService.ThrowOnGetConfig = new HttpRequestException("offline");

        await _effect.LoadUsersAsync();

        _userIdentityStore.State.AvailableUsers.ShouldBeEmpty();
        _userIdentityStore.State.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public async Task Dispatch_Initialize_RunsTheSameWork()
    {
        GivenUsers("alice");

        _dispatcher.Dispatch(new Initialize());

        await TestChat.Eventually(() => _userIdentityStore.State.AvailableUsers.Count == 1);
        _userIdentityStore.State.AvailableUsers.Single().Id.ShouldBe("alice");
    }

    [Fact]
    public async Task Dispatch_SelectUser_PersistsTheChoice()
    {
        _dispatcher.Dispatch(new SelectUser("alice"));

        await TestChat.Eventually(() => _localStorage.Values.ContainsKey("selectedUserId"));
        _localStorage.Values["selectedUserId"].ShouldBe("alice");
    }

    [Fact]
    public async Task Dispatch_Initialize_FaultIsLoggedRatherThanDiscarded()
    {
        _configService.ThrowOnGetConfig = new InvalidOperationException("config unavailable");

        _dispatcher.Dispatch(new Initialize());

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("config unavailable");
    }

    [Fact]
    public async Task Disposed_StopsHandlingSelectUser()
    {
        _effect.Dispose();

        _dispatcher.Dispatch(new SelectUser("alice"));

        await Task.Delay(50);
        _localStorage.Values.ShouldNotContainKey("selectedUserId");
    }

    private void GivenUsers(params string[] userIds) =>
        _configService.Config = new AppConfig(null, userIds.Select(id => new UserConfig(id, "")).ToArray());

    public void Dispose()
    {
        _effect.Dispose();
        _userIdentityStore.Dispose();
    }
}