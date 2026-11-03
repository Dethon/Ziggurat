using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

// What this person may see — their roles, whether they see all conversations — the hub reads once
// per connection, off the assertion the proxy signed as it opened, and joins its groups by. Told the
// household's people changed, the page opens a fresh connection and asks for its list again, so a
// grant shows the household's topics and a withdrawal takes them away without a reload; a member
// disabled meanwhile is refused that connection and sent to sign in, as on any reconnect.
public sealed class AccessChangedEffect : IDisposable
{
    private readonly IDisposable _registration;

    public AccessChangedEffect(Dispatcher dispatcher, IChatLiveConnection connection, ILogger<AccessChangedEffect> logger)
    {
        _registration = dispatcher.RegisterHandler<AccessChanged>(_ =>
            RenewAsync(dispatcher, connection).LogFaults(logger, nameof(AccessChanged)));
    }

    private static async Task RenewAsync(Dispatcher dispatcher, IChatLiveConnection connection)
    {
        await connection.RenewAsync();
        dispatcher.Dispatch(new ReloadTopics());
    }

    public void Dispose() => _registration.Dispose();
}