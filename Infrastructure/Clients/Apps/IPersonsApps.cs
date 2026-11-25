using Domain.Apps;

namespace Infrastructure.Clients.Apps;

// A person's apps for WebChat's space switcher, as the apps server lists them (PersonsApps): their own
// published apps, and the links of the household's apps named as shared. Asked for the session a chat
// connection signed in with — the accounts service's word for that session's person on
// AppChannels.PersonsApps, carried to the apps server — never for an account by its id. Null is "not
// asked just now": no apps server here, the session gone, an outage, a refusal or a slow answer, which
// costs the switcher its apps and nothing else.
public interface IPersonsApps
{
    Task<IReadOnlyList<PersonsApp>?> ListAsync(
        string userId, string sessionId, IReadOnlyCollection<string> shared, CancellationToken ct);
}