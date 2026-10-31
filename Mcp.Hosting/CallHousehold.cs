using Domain.Contracts;
using Domain.DTOs.Household;
using Microsoft.Extensions.DependencyInjection;

namespace Mcp.Hosting;

// The one place a shared server reads its section of a household's configuration
// (.claude/rules/household-config.md): over something signed that names the household — the call
// token the gate verified (call.Household, CallTurn), the accounts service's assertion of the person
// a server's own app serves, or the delegation stored with a schedule that fires with no call — and
// never over a household the server names, since the accounts service believes only the proof. What
// it is answered is the reader's to keep and drop (IHouseholdConfiguration). A tool reads
// configuration through here and nowhere else (CallHouseholdConformanceTests), so none can ask about
// a household its call did not prove.
public sealed class CallHousehold : ICallHousehold
{
    private readonly HouseholdProof _proof;
    private readonly Func<IHouseholdConfiguration> _configuration;

    private CallHousehold(string id, HouseholdProof proof, Func<IHouseholdConfiguration> configuration)
    {
        Id = id;
        _proof = proof;
        _configuration = configuration;
    }

    public string Id { get; }

    public Task<HouseholdSettings> SettingsAsync(CancellationToken ct) => _configuration().SettingsAsync(Id, _proof, ct);

    public Task<IdealistaKeys?> IdealistaKeysAsync(CancellationToken ct) => _configuration().IdealistaKeysAsync(Id, _proof, ct);

    // A person the server's own app serves, on the assertion the proxy's session check signed for its
    // channel (Sugary's /api).
    public static CallHousehold OfPerson(string householdId, string assertion, IHouseholdConfiguration configuration) =>
        new(householdId, HouseholdProof.OfAssertion(assertion), () => configuration);

    // A schedule's fire, which no call raised: the delegation the agent signed for its last write
    // names the person behind it, and so their household.
    public static CallHousehold OfFire(string householdId, string delegation, IHouseholdConfiguration configuration) =>
        new(householdId, HouseholdProof.OfDelegation(delegation), () => configuration);

    // Resolved when a section is asked for, not when the call arrives: every filesystem server hands
    // its mount the call's household, and only the servers that read configuration register a reader.
    internal static CallHousehold OfCall(string householdId, string verifiedToken, IServiceProvider services) =>
        new(householdId, HouseholdProof.OfCallToken(verifiedToken), () =>
            services.GetService<IHouseholdConfiguration>()
            ?? throw new InvalidOperationException(
                "this server reads no household configuration: its module registers none (AddHouseholdConfiguration)"));
}