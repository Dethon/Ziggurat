using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// What a person may see is read by the hub once per connection, off the assertion the proxy signed as
// it opened. Told their household's people changed — a role, "sees all conversations", somebody
// disabled — the page opens a fresh connection, which carries what they may see now, and then asks
// for its list again.
public sealed class AccessChangedEffectTests
{
    // The person is asked for again too: their roles say whether they see all, which decides whether
    // the list is narrowed to their own and offers whose to list (TopicFilterEffect).
    [Fact]
    public async Task AccessChanged_RenewsTheConnection_ThenAsksWhoIsSignedIn_AndReloadsTheTopics()
    {
        var calls = new CallRecorder();
        var dispatcher = new Dispatcher();
        var connection = new FakeChatLiveConnection(calls);
        dispatcher.RegisterHandler<LoadSignedIn>(_ => calls.Record("signed-in"));
        dispatcher.RegisterHandler<ReloadTopics>(_ => calls.Record("reload"));
        using var effect = new AccessChangedEffect(dispatcher, connection, new RecordingLogger<AccessChangedEffect>());

        dispatcher.Dispatch(new AccessChanged());

        await Tests.Eventually.Until(() => calls.Calls.Contains("reload"), "the topics to be reloaded");
        calls.Calls.ShouldBe(["renew", "signed-in", "reload"]);
    }
}