using JetBrains.Annotations;

namespace Domain.DTOs.Admin;

// The admin API on the accounts service (.claude/rules/admin.md), spelled once for the service and
// for the two browser clients that call it: the owner's admin area in the chat client and the
// operator's household list in the dashboard. Every route sits under one of two roots, which decide
// who may call it: /household acts on the household the caller's assertion names and is its owners',
// /operator acts across households and is the operator's.
public static class AdminProtocol
{
    public const string Root = "/accounts/admin";
    public const string Household = Root + "/household";
    public const string Operator = Root + "/operator";

    public const string Settings = Household + "/settings";
    public const string HouseholdChanges = Household + "/changes";
    public const string AllChanges = Operator + "/changes";

    // The operator's household list: GET lists every household, POST creates one with its owner.
    public const string Households = Operator + "/households";

    // A recovery link for one owner of one household, minted by the operator.
    public const string OwnerRecoveryRoute = Households + "/{householdId}/owners/{userId}/recovery";

    public static string OwnerRecovery(string householdId, string userId) =>
        $"{Households}/{Uri.EscapeDataString(householdId)}/owners/{Uri.EscapeDataString(userId)}/recovery";

    // Every admin request carries this header with this value. A cross-site form cannot set a
    // header, and a cross-origin script that does is preflighted, which nothing here answers: so a
    // request carrying it came from a page on the host the API is served on.
    public const string RequestHeader = "X-Requested-With";
    public const string RequestHeaderValue = "ziggurat-admin";

    // How many changes a read answers when it names no limit, and the most it ever answers.
    public const int DefaultChangeCount = 50;
    public const int MaxChangeCount = 200;
}

// What a refused change says: a sentence for the person and, for a value the section's rules
// refused, each problem as the rules phrase it. Never a value that was sent.
[PublicAPI]
public sealed record AdminRefusal(string Message, IReadOnlyList<string> Problems);

// A change that stood, with what is now saved, and a warning when it stood with a caveat (its
// readers not told at once, a key the provider could not be asked about).
[PublicAPI]
public sealed record AdminSaved<T>(T Value, string? Warning = null);

// One recorded change: who made it (their account, and the name they go by now), what (the route it
// came on and a summary that never carries a secret), on which household, and when.
[PublicAPI]
public sealed record AdminChangeEntry(
    string Id,
    string HouseholdId,
    string ActorUserId,
    string ActorTenantId,
    string ActorName,
    string Route,
    string Summary,
    DateTimeOffset At);
// A household the operator creates: its name and the display name of the owner it starts with.
[PublicAPI]
public sealed record NewHousehold(string Name, string OwnerName);

// A link that enrolls a passkey for one owner of a household — the owner a new household starts
// with, or one the operator recovers. Shown once, in the answer that minted it: only its hash is kept.
[PublicAPI]
public sealed record OwnerLink(string HouseholdId, string UserId, string DisplayName, string Url, DateTimeOffset ExpiresAt);

// One household as the operator's list shows it: its people by role, the servers connected to the
// agent's relay now (null when the agent could not be asked), and when any of its people's sessions
// was last seen by the session check (null when none ever was).
[PublicAPI]
public sealed record HouseholdListEntry(
    string Id,
    string Name,
    bool IsHome,
    DateTimeOffset CreatedAt,
    IReadOnlyList<HouseholdPerson> Owners,
    IReadOnlyList<HouseholdPerson> Members,
    IReadOnlyList<HouseholdConnectedServer>? ConnectedServers,
    DateTimeOffset? LastSeenAt);

[PublicAPI]
public sealed record HouseholdPerson(string UserId, string DisplayName, bool IsOperator);

[PublicAPI]
public sealed record HouseholdConnectedServer(string Name, DateTimeOffset ConnectedSince);