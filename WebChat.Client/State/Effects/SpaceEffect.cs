using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class SpaceEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ITopicService _topicService;
    private readonly ConnectionStore _connectionStore;
    private readonly NavigationManager _navigationManager;
    private readonly IPushSubscriptionService _pushNotificationService;
    private readonly ILogger<SpaceEffect> _logger;
    private readonly IDisposable _handlerRegistration;
    private readonly IDisposable _validatedRegistration;
    private string _previousSlug = "default";

    public SpaceEffect(
        Dispatcher dispatcher,
        ITopicService topicService,
        ConnectionStore connectionStore,
        NavigationManager navigationManager,
        IPushSubscriptionService pushNotificationService,
        ILogger<SpaceEffect> logger)
    {
        _dispatcher = dispatcher;
        _topicService = topicService;
        _connectionStore = connectionStore;
        _navigationManager = navigationManager;
        _pushNotificationService = pushNotificationService;
        _logger = logger;

        _handlerRegistration = dispatcher.RegisterHandler<SelectSpace>(
            action => HandleSelectSpaceAsync(action.Slug).LogFaults(_logger, nameof(SelectSpace)));
        _validatedRegistration = dispatcher.RegisterHandler<SpaceValidated>(action => FollowTheSpace(action.Slug));
    }

    // First load opens the household's first space when the address names none of them (a space
    // the household removed), and the address must say so too, or a reload or a shared link names
    // the removed space again. The space it names is the one just joined, so the selection the
    // corrected address makes is not a switch.
    private void FollowTheSpace(string slug)
    {
        _previousSlug = slug;
        var path = _navigationManager.ToBaseRelativePath(_navigationManager.Uri).Split('?', '#')[0].Trim('/');
        var named = path.Length == 0 ? SpaceConfig.DefaultSlug : Uri.UnescapeDataString(path);
        if (named != slug)
        {
            _navigationManager.NavigateTo(slug == SpaceConfig.DefaultSlug ? "/" : $"/{slug}", replace: true);
        }
    }

    public async Task HandleSelectSpaceAsync(string slug)
    {
        var previousSlug = _previousSlug;
        _previousSlug = slug;

        if (slug == previousSlug)
        {
            return;
        }

        // Before the hub is up this is the first navigation rather than a switch the user made
        // mid-session: InitializationEffect joins the slug the SelectSpace reducer has already
        // stored and validates it, so there is nothing to do and nothing to say here.
        if (_connectionStore.State.Status != ConnectionStatus.Connected)
        {
            return;
        }

        // The household's spaces are the hub's to say. Not live is the same failure as a join that
        // could not be made, below, and is said the same way.
        var spaces = await _topicService.GetSpacesAsync();
        if (!spaces.IsLive)
        {
            _previousSlug = previousSlug;
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        var household = spaces.Value ?? [];
        if (household.FirstOrDefault(s => s.Slug == slug) is not { } space)
        {
            _dispatcher.Dispatch(new InvalidSpace());
            _navigationManager.NavigateTo(SpaceAgent.PathOf(household.FirstOrDefault()), replace: true);
            return;
        }

        var joined = await _topicService.JoinSpaceAsync(slug);

        // The join is what puts this browser in the server's space group. Clearing the sidebar
        // and validating the space without it would show an empty space the server never moved
        // us to, so this says so once (ADR-0004) and commits nothing — including the slug it
        // remembers, so the next attempt at the same space is a switch again and not a no-op.
        if (!joined.IsLive)
        {
            _previousSlug = previousSlug;
            _dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return;
        }

        try
        { await _pushNotificationService.ResubscribeAsync(); }
        catch { /* best-effort — don't block space transition */ }

        _dispatcher.Dispatch(new TopicsLoaded([]));
        _dispatcher.Dispatch(new ClearAllMessages());
        _dispatcher.Dispatch(new SpaceValidated(slug, space.Name, space.AccentColor, space.DefaultAgent));
    }

    public void Dispose()
    {
        _handlerRegistration.Dispose();
        _validatedRegistration.Dispose();
    }
}