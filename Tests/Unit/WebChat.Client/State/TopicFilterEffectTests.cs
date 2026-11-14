using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// Whose topics the list shows. Someone who sees all reads their own by default, and what they pick
// is remembered on this device; anyone else is shown everything they see, which is theirs anyway.
public sealed class TopicFilterEffectTests : IDisposable
{
    private static readonly SignedInPerson Olga = new("olga", "Olga", null, "home", ["member", "sees-all"]);
    private static readonly SignedInPerson Laura = new("laura", "Laura", null, "home", ["member"]);

    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly UserIdentityStore _identityStore;
    private readonly FakeTopicService _topicService = new();
    private readonly FakeLocalStorageService _storage = new();
    private readonly TopicFilterEffect _effect;

    public TopicFilterEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _identityStore = new UserIdentityStore(_dispatcher);
        _topicService.People.AddRange([new HouseholdMemberName("fran", "Fran"), new HouseholdMemberName("laura", "Laura")]);
        _effect = new TopicFilterEffect(
            _dispatcher, _topicsStore, _identityStore, _topicService, _storage, new RecordingLogger<TopicFilterEffect>());
    }

    [Fact]
    public async Task SomeoneWhoSeesAll_ReadsTheirOwnByDefault_AndIsNamedTheHouseholdsPeople()
    {
        _dispatcher.Dispatch(new SignedIn(Olga));

        await Eventually.Until(() => _topicsStore.State.People.Count == 2, "the household's people to be named");
        _topicsStore.State.Filter.ShouldBe(TopicFilter.Mine);
    }

    [Fact]
    public async Task SomeoneWhoSeesAll_ReadsWhatTheyLastPickedOnThisDevice()
    {
        _storage.Seed(TopicFilterEffect.StorageKey, "of:fran");

        _dispatcher.Dispatch(new SignedIn(Olga));

        await Eventually.Until(() => _topicsStore.State.Filter == TopicFilter.Of("fran"), "the remembered pick");
    }

    [Fact]
    public async Task APick_IsListedAndRemembered()
    {
        _dispatcher.Dispatch(new SignedIn(Olga));
        await Eventually.Until(() => _topicsStore.State.Filter == TopicFilter.Mine, "the default");

        _dispatcher.Dispatch(new ChooseTopicFilter(TopicFilter.Everyone));

        await Eventually.Until(() => _topicsStore.State.Filter == TopicFilter.Everyone, "the pick to be listed");
        await Eventually.Until(
            () => _storage.Values.GetValueOrDefault(TopicFilterEffect.StorageKey) == "everyone", "the pick to be remembered");
    }

    // A member who does not see all has nobody else's private topics to sort, so nothing narrows their
    // list — whatever an earlier grant left remembered on the device.
    [Fact]
    public async Task SomeoneWhoDoesNotSeeAll_IsShownEverythingTheySee_AndNamedNobody()
    {
        _storage.Seed(TopicFilterEffect.StorageKey, "of:fran");
        _dispatcher.Dispatch(new FilterTopics(TopicFilter.Mine));

        _dispatcher.Dispatch(new SignedIn(Laura));

        await Eventually.Until(() => _topicsStore.State.Filter == TopicFilter.Everyone, "the list to be everything");
        _topicsStore.State.People.ShouldBeEmpty();
    }

    // The people are the hub's to name, and the hub answers once the page is connected: a first ask
    // made before then is asked again when the agents arrive, which is after connecting.
    [Fact]
    public async Task ThePeople_AreAskedAgainOnceConnected_WhenTheFirstAskFoundNoConnection()
    {
        _topicService.NotLive = true;
        _dispatcher.Dispatch(new SignedIn(Olga));
        await Eventually.Settle();
        _topicsStore.State.People.ShouldBeEmpty();

        _topicService.NotLive = false;
        _dispatcher.Dispatch(new SetAgents([]));

        await Eventually.Until(() => _topicsStore.State.People.Count == 2, "the people to be named once connected");
    }

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _identityStore.Dispose();
    }
}