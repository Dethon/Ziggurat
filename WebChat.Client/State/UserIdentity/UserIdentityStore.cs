using WebChat.Client.Models;

namespace WebChat.Client.State.UserIdentity;

public record SignedIn(SignedInPerson Person) : IAction;

public sealed class UserIdentityStore : IDisposable
{
    private readonly Store<UserIdentityState> _store;

    public UserIdentityStore(Dispatcher dispatcher)
    {
        _store = new Store<UserIdentityState>(UserIdentityState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public UserIdentityState State => _store.State;

    public IObservable<UserIdentityState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static UserIdentityState Reduce(UserIdentityState state, IAction action) => action switch
    {
        SignedIn a => state with { SignedIn = a.Person },
        _ => state
    };
}