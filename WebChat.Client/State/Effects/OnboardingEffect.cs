using Domain.DTOs.Admin;
using Microsoft.AspNetCore.Components;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Layout;
using WebChat.Client.Models;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.State.Effects;

// An owner whose household has no OpenRouter key of its own lands on the keys page when the chat
// loads: until one is set its agents refuse every turn, so it is the first thing to do. The home
// household's agents run on the deployment's key, so its owners never land there. Asked only for an
// owner, and never from the admin area itself, which asks who is signed in too.
public sealed class OnboardingEffect : IDisposable
{
    private readonly IAdminService _admin;
    private readonly NavigationManager _navigation;
    private readonly IDisposable _registration;

    public OnboardingEffect(
        Dispatcher dispatcher, IAdminService admin, NavigationManager navigation, ILogger<OnboardingEffect> logger)
    {
        _admin = admin;
        _navigation = navigation;
        _registration = dispatcher.RegisterHandler<SignedIn>(action =>
            LandAsync(action.Person).LogFaults(logger, nameof(OnboardingEffect)));
    }

    public async Task LandAsync(SignedInPerson person)
    {
        if (!person.IsOwner || AdminArea.Contains(_navigation))
        {
            return;
        }

        if (await _admin.GetKeysAsync() is AdminAnswer<HouseholdKeys>.Answered { Value.NeedsOpenRouterKey: true })
        {
            AdminArea.Enter(_navigation, AdminArea.Keys);
        }
    }

    public void Dispose() => _registration.Dispose();
}