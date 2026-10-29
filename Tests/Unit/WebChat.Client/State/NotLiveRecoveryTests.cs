using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// A call the client made for its own reasons says nothing when it could not be made. The user
// asked for none of them and can do nothing about them; they are retried on becoming live.
public sealed class NotLiveRecoveryTests
{
    [Fact]
    public async Task SessionRecovery_WhileNotLive_RaisesNoToastAndDisturbsNoStore()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new TopicsLoaded([]));

        client.GoNotLive();
        await client.Service<ISessionRecovery>().RecoverAsync();

        client.Toasts.State.Toasts.ShouldBeEmpty();
        client.Topics.State.Topics.ShouldBeEmpty();
    }

    // The connection is its person from the proxy's headers, so there is nobody to name again:
    // recovery asks for the space and nothing about who this is.
    [Fact]
    public async Task SessionRecovery_WhileLive_RejoinsTheSpaceAndNamesNobody()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();

        await client.Service<ISessionRecovery>().RecoverAsync();

        transport.Calls.ShouldContain(call => call.MethodName == "JoinSpace");
        transport.Calls.ShouldNotContain(call => call.MethodName == "RegisterUser");
    }
}