namespace Domain.DTOs.Tunnel;

// The names a household's servers go by when the deployment itself must find one — a credential is
// minted for the name, the tunnel claims it, and the relay looks it up — rather than an agent
// definition naming it. The tenant kit runs its servers under these.
public static class HouseholdServerNames
{
    // Its pages listener answers the browse pages a person opens from a link.
    public const string WebSearch = "websearch";

    // Its announce, dismiss and satellite API is what the timers server rings and validates through.
    public const string Voice = "voice";
}