using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

// Whose topics the list shows. Someone who sees every topic of the household reads their own unless
// they pick more — everyone's, or one person's — and the pick is remembered on this device; anyone
// else is shown all they see, since nothing they see is anybody else's private topic. The hub names
// the household's people for the picker, and only once connected, so they are asked again when the
// agents arrive, which follows connecting.
public sealed class TopicFilterEffect : IDisposable
{
    public const string StorageKey = "topicFilter";

    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly UserIdentityStore _identityStore;
    private readonly ITopicService _topicService;
    private readonly ILocalStorageService _localStorage;
    private readonly IDisposable _signedInRegistration;
    private readonly IDisposable _agentsRegistration;
    private readonly IDisposable _chooseRegistration;

    public TopicFilterEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        UserIdentityStore identityStore,
        ITopicService topicService,
        ILocalStorageService localStorage,
        ILogger<TopicFilterEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _identityStore = identityStore;
        _topicService = topicService;
        _localStorage = localStorage;

        _signedInRegistration = dispatcher.RegisterHandler<SignedIn>(
            action => SettleAsync(action.Person).LogFaults(logger, nameof(SignedIn)));
        _agentsRegistration = dispatcher.RegisterHandler<SetAgents>(
            _ => LoadPeopleIfMissingAsync().LogFaults(logger, nameof(SetAgents)));
        _chooseRegistration = dispatcher.RegisterHandler<ChooseTopicFilter>(
            action => ChooseAsync(action.Filter).LogFaults(logger, nameof(ChooseTopicFilter)));
    }

    private async Task SettleAsync(SignedInPerson person)
    {
        if (person.Viewer?.SeesAll != true)
        {
            Apply(TopicFilter.Everyone);
            _dispatcher.Dispatch(new HouseholdPeopleLoaded([]));
            return;
        }

        Apply(TopicFilterChoice.Parse(await _localStorage.GetAsync(StorageKey)));
        await LoadPeopleIfMissingAsync();
    }

    private async Task ChooseAsync(TopicFilter filter)
    {
        Apply(filter);
        await _localStorage.SetAsync(StorageKey, TopicFilterChoice.Format(filter));
    }

    private async Task LoadPeopleIfMissingAsync()
    {
        if (_identityStore.State.SignedIn?.Viewer?.SeesAll != true || _topicsStore.State.People.Count > 0)
        {
            return;
        }

        if (await _topicService.GetHouseholdPeopleAsync() is { IsLive: true, Value: { } people })
        {
            _dispatcher.Dispatch(new HouseholdPeopleLoaded(people));
        }
    }

    // A filter already showing is not read again: settling the same one on every sign-in would empty
    // the list under the person for nothing.
    private void Apply(TopicFilter filter)
    {
        if (_topicsStore.State.Filter != filter)
        {
            _dispatcher.Dispatch(new FilterTopics(filter));
        }
    }

    public void Dispose()
    {
        _signedInRegistration.Dispose();
        _agentsRegistration.Dispose();
        _chooseRegistration.Dispose();
    }
}