using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// The client's person is whoever the accounts service says is signed in: nothing is picked and
// nothing is kept in the browser, and a page whose session is gone goes to sign in and back.
public sealed class UserIdentityEffectTests : IDisposable
{
    private static readonly SignedInPerson _laura = new("0123abcd", "Laura", "https://example/laura.png");

    private readonly Dispatcher _dispatcher = new();
    private readonly UserIdentityStore _userIdentityStore;
    private readonly FakeAccountService _accounts = new();
    private readonly RecordingLogger<UserIdentityEffect> _logger = new();
    private readonly UserIdentityEffect _effect;

    public UserIdentityEffectTests()
    {
        _userIdentityStore = new UserIdentityStore(_dispatcher);

        _effect = new UserIdentityEffect(_dispatcher, _accounts, _logger);
    }

    [Fact]
    public async Task LoadSignedInAsync_ASession_PublishesThePerson()
    {
        _accounts.SignedIn = _laura;

        await _effect.LoadSignedInAsync();

        _userIdentityStore.State.SignedIn.ShouldBe(_laura);
        _accounts.SentToSignIn.ShouldBe(0);
    }

    [Fact]
    public async Task LoadSignedInAsync_NoSession_SendsThePageToSignIn()
    {
        await _effect.LoadSignedInAsync();

        _userIdentityStore.State.SignedIn.ShouldBeNull();
        _accounts.SentToSignIn.ShouldBe(1);
    }

    // The hub's check refused the connection: the page is loaded again through the proxy, whose same
    // check sends a signed-out person to sign in and a suspended household to its notice.
    [Fact]
    public void TheHubRefusingTheSession_LoadsThePageAgainThroughTheProxy()
    {
        _dispatcher.Dispatch(new HubSessionRefused());

        _accounts.Reloaded.ShouldBe(1);
    }

    // Offline says nothing about the session, and a sign-in page would not load either.
    [Fact]
    public async Task LoadSignedInAsync_Unreachable_PublishesNobodyAndStays()
    {
        _accounts.ThrowOnGetSignedIn = new HttpRequestException("offline");

        await _effect.LoadSignedInAsync();

        _userIdentityStore.State.SignedIn.ShouldBeNull();
        _accounts.SentToSignIn.ShouldBe(0);
    }

    [Fact]
    public async Task Dispatch_Initialize_RunsTheSameWork()
    {
        _accounts.SignedIn = _laura;

        _dispatcher.Dispatch(new Initialize());

        await TestChat.Eventually(() => _userIdentityStore.State.SignedIn is not null);
        _userIdentityStore.State.SignedIn.ShouldBe(_laura);
    }

    [Fact]
    public async Task Dispatch_Initialize_FaultIsLoggedRatherThanDiscarded()
    {
        _accounts.ThrowOnGetSignedIn = new InvalidOperationException("accounts unavailable");

        _dispatcher.Dispatch(new Initialize());

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("accounts unavailable");
    }

    public void Dispose()
    {
        _effect.Dispose();
        _userIdentityStore.Dispose();
    }
}