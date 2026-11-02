using Domain.DTOs;
using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Contracts;

// What every view of a channel connection shares: its channel id and the household it serves. A
// channel id alone no longer names one connection — two households' voice servers are both
// `voice` — so whatever picks a connection picks it by both (HouseholdChannels.Serving).
[PublicAPI]
public interface IHouseholdChannel
{
    string ChannelId { get; }

    // Every household, unless the connection is a household's own server's (ChannelHousehold).
    ChannelHousehold Household => ChannelHousehold.Every;
}

public static class HouseholdChannels
{
    // The connections a turn of this household may use: the deployment's own, and that household's
    // (the home's for a turn naming none). The one place the rule is applied, so no caller takes
    // "the first connection with that id". Lazy on purpose: over the live set, an approval relay that
    // keeps it reads the connections as each question is asked, a household's newest included.
    public static IEnumerable<T> Serving<T>(this IEnumerable<T> connections, string? householdId)
        where T : IHouseholdChannel =>
        connections.Where(c => HouseholdOf(c).Serves(householdId));

    public static T? Find<T>(this IEnumerable<T> connections, string channelId, string? householdId)
        where T : IHouseholdChannel =>
        connections.Serving(householdId)
            .Where(c => c.ChannelId == channelId)
            // The household's own before the deployment's, should a channel id ever be both.
            .OrderBy(c => HouseholdOf(c).IsEvery ? 1 : 0)
            .FirstOrDefault();

    // The household an admitted turn is served in: the one whose own connection it arrived on, else
    // the one its verified person belongs to, else the one a fire's server keeps the object under (a
    // household's schedule whose delegation no longer holds runs as nobody, but is still that
    // household's). A turn naming none acts for nobody and runs the home's agents, so the home's
    // connections serve it.
    public static string? HouseholdOfTurn(ChannelMessage message, IHouseholdChannel origin) =>
        HouseholdOf(origin).HouseholdId ?? message.Principal?.Account?.TenantId ?? message.HouseholdId;

    // A test double that leaves the member unset answers null; it is the deployment's.
    public static ChannelHousehold HouseholdOf(IHouseholdChannel channel) =>
        channel.Household ?? ChannelHousehold.Every;
}