using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// The app loaded again in the same window — back from a login page the agent sent, back from the
// account page, reloaded — opens the conversation it had open. An installed app has no address bar
// to say which, and landing on the list left the person to find where the agent's reply was arriving.
public sealed class OpenTopicEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topics;
    private readonly SpaceStore _space;
    private readonly FakeOpenTopicMemory _memory = new();
    private readonly FakeLaunchLink _launch = new();
    private readonly OpenTopicEffect _effect;

    public OpenTopicEffectTests()
    {
        _topics = new TopicsStore(_dispatcher);
        _space = new SpaceStore(_dispatcher);
        _effect = new OpenTopicEffect(_dispatcher, _topics, _space, _memory, _launch, new RecordingLogger<OpenTopicEffect>());
    }

    [Fact]
    public async Task OpeningAConversation_IsRememberedForThisSpace_AndANewChatForgetsIt()
    {
        _dispatcher.Dispatch(new SelectTopic("topic-2"));
        await TestChat.Eventually(() => _memory.Remembered.GetValueOrDefault("default") == "topic-2");

        _dispatcher.Dispatch(new SelectTopic(null));
        await TestChat.Eventually(() => !_memory.Remembered.ContainsKey("default"));
    }

    [Fact]
    public async Task TheFirstTopicsLoaded_ReopenTheConversationThisWindowHadOpen()
    {
        _memory.Remembered["default"] = "topic-2";

        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1"), Topic("topic-2")]));

        await TestChat.Eventually(() => _topics.State.SelectedTopicId == "topic-2");
    }

    [Fact]
    public async Task AConversationNoLongerListed_OpensNothing()
    {
        _memory.Remembered["default"] = "topic-gone";

        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1")]));
        await Tests.Eventually.Settle();

        _topics.State.SelectedTopicId.ShouldBeNull();
    }

    // Only the load that opens the app: a later reload of the list (a reconnect, an archive toggle)
    // never takes the person somewhere they did not ask to go.
    [Fact]
    public async Task ALaterLoad_LeavesTheConversationOnScreenAlone()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1"), Topic("topic-2")]));
        await Tests.Eventually.Settle();
        _memory.Remembered["default"] = "topic-2";

        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1"), Topic("topic-2")]));
        await Tests.Eventually.Settle();

        _topics.State.SelectedTopicId.ShouldBeNull();
    }

    // A notification's tap names the conversation its reply finished in, and that is what the person
    // asked to see — not the one this window had open before.
    [Fact]
    public async Task TheFirstTopicsLoaded_OpenTheConversationTheAddressNames_OverTheRememberedOne()
    {
        _memory.Remembered["default"] = "topic-2";
        _launch.Topic = new LaunchTopic("agent-1", "topic-1");

        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1"), Topic("topic-2")]));

        await TestChat.Eventually(() => _topics.State.SelectedTopicId == "topic-1");
        _launch.Spent.ShouldBe(1);
    }

    // A conversation the list does not hold (archived, past the first page, left out by the list's
    // filter) opens nothing — not the remembered one, which is not what the tap was about. The app
    // lands on the space's list.
    [Fact]
    public async Task AnAddressNamingAConversationNotListed_OpensNothing()
    {
        _memory.Remembered["default"] = "topic-2";
        _launch.Topic = new LaunchTopic("agent-1", "topic-gone");

        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1"), Topic("topic-2")]));
        await Tests.Eventually.Settle();

        _topics.State.SelectedTopicId.ShouldBeNull();
        _launch.Spent.ShouldBe(1);
    }

    private static StoredTopic Topic(string id) => StoredTopic.FromMetadata(TestChat.Topic(id));

    public void Dispose() => _effect.Dispose();
}