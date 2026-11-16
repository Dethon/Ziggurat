namespace Domain.Contracts;

// A household's own voice server as the timers server reaches it — through the relay, naming the
// household — so a timer rings, is dismissed and is validated on its own household's satellites and
// never on another's.
public interface IHouseholdVoices
{
    HouseholdVoice For(string tenantId);
}

public sealed record HouseholdVoice(IInsistentAnnouncer Announcer, IAlertDismisser Dismisser, ISatelliteCatalog Satellites);