namespace Domain.DTOs.Channel;

// Whom a channel connection serves. A channel the deployment dials by URL (the chat client,
// Telegram, the scheduling server) is one connection serving every household; a household's own
// server reached through its tunnel (voice, Home Assistant, the library, web search) is a
// connection of that household's alone, and what arrives on it or is sent back on it stays
// that household's.
//
// The home is marked because a turn acting for nobody runs the home's agents (a fire nobody
// vouched for, a watch of the home's), so the home's own connections are the ones that serve it.
public sealed record ChannelHousehold(string? HouseholdId, bool Home = false)
{
    public static readonly ChannelHousehold Every = new((string?)null);

    public static ChannelHousehold Own(string householdId, bool home) => new(householdId, home);

    public bool IsEvery => HouseholdId is null;

    // Whether a turn of this household may be served here: a deployment channel serves everyone, a
    // household's own serves that household, and the home's serves a turn that names none.
    public bool Serves(string? householdId) =>
        HouseholdId is null || (householdId is null ? Home : HouseholdId == householdId);

    public override string ToString() => HouseholdId ?? "every household";
}