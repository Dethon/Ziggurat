using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

// The client's person is whoever the accounts service says is signed in. Nothing is picked and
// nothing is kept in the browser: the proxy already let this page through as somebody, and the hub
// already knows who, so this is only the name and the face to draw.
public sealed class UserIdentityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IAccountService _accounts;
    private readonly IDisposable _initializeRegistration;
    private readonly IDisposable _loadRegistration;
    private readonly IDisposable _refusedRegistration;

    public UserIdentityEffect(Dispatcher dispatcher, IAccountService accounts, ILogger<UserIdentityEffect> logger)
    {
        _dispatcher = dispatcher;
        _accounts = accounts;

        _initializeRegistration = dispatcher.RegisterHandler<Initialize>(
            _ => LoadSignedInAsync().LogFaults(logger, nameof(Initialize)));
        _loadRegistration = dispatcher.RegisterHandler<LoadSignedIn>(
            _ => LoadSignedInAsync().LogFaults(logger, nameof(LoadSignedIn)));

        // The hub's session check refused the connection, and the page sits behind the same check:
        // loaded again, it becomes the sign-in page, the disabled account's or the suspended
        // household's notice, whichever the proxy now says.
        _refusedRegistration = dispatcher.RegisterHandler<HubSessionRefused>(_ => _accounts.Reload());
    }

    public async Task LoadSignedInAsync()
    {
        try
        {
            if (await _accounts.GetSignedInAsync() is { } person)
            {
                _dispatcher.Dispatch(new SignedIn(person));
                return;
            }
        }
        catch (HttpRequestException)
        {
            // Unreachable says nothing about the session, and a sign-in page would not load either.
            return;
        }

        // A page that loaded with no session behind it — kept by the service worker, or signed out
        // since — has nobody to be: sign in and come back to it.
        _accounts.SendToSignIn();
    }

    public void Dispose()
    {
        _initializeRegistration.Dispose();
        _loadRegistration.Dispose();
        _refusedRegistration.Dispose();
    }
}