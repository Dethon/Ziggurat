using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using McpChannelTelegram.Services;

namespace Tests.Unit.McpChannelTelegram;

// The households' configuration as the Telegram server reads it: every household's bots at once, and
// a household's agent names on the assertion of a person of it on Telegram — refused when the proof
// names another household, as the accounts service refuses it. A household that saved no agents lists
// the two templates, as the service answers. It can be made unreachable, as the service is in an outage.
internal sealed class FakeTelegramHouseholds : IHouseholdConfiguration
{
    private volatile IReadOnlyList<HouseholdTelegramBot> _bots = [];

    public ConcurrentDictionary<string, AgentsSection> Agents { get; } = new();

    public bool Unreachable { get; set; }

    public void SaveBots(params HouseholdTelegramBot[] bots) => _bots = bots;

    public Task<IReadOnlyList<HouseholdTelegramBot>> TelegramBotsAsync(CancellationToken ct) =>
        Unreachable
            ? Task.FromException<IReadOnlyList<HouseholdTelegramBot>>(new HttpRequestException("the accounts service is down"))
            : Task.FromResult(_bots);

    public async Task<HouseholdAgentNames> AgentNamesAsync(string householdId, HouseholdProof proof, CancellationToken ct)
    {
        if (Unreachable)
        {
            throw new HttpRequestException("the accounts service is down");
        }

        var person = await TestAssertions.Verifier().VerifyAsync(proof.Assertion, ChannelIds.Telegram, ct)
                     ?? throw Refused("the proof is no assertion of anybody on telegram");
        return person.TenantId == householdId
            ? HouseholdAgents.Names(Agents.GetValueOrDefault(householdId) ?? new AgentsSection())
            : throw Refused($"the proof names household {person.TenantId}, not {householdId}");
    }

    public Task<AgentHouseholdConfig> ForAgentAsync(string householdId, CancellationToken ct) =>
        throw new NotSupportedException("the Telegram server reads its bots and agent names alone");

    public Task<HouseholdSettings> SettingsAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the Telegram server reads its bots and agent names alone");

    public Task<IdealistaKeys?> IdealistaKeysAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the Telegram server reads its bots and agent names alone");

    public Task<SpacesSection> SpacesAsync(string householdId, HouseholdProof proof, CancellationToken ct) =>
        throw new NotSupportedException("the Telegram server reads its bots and agent names alone");

    public Task<IReadOnlyList<HouseholdMembers>> PeopleAsync(CancellationToken ct) =>
        throw new NotSupportedException("the Telegram server reads its bots and agent names alone");

    private static HouseholdConfigRefusedException Refused(string detail) =>
        new(new HouseholdConfigRefused(HouseholdConfigRefusal.Unproven, detail));
}

// The chats' picks as the store keeps them, in memory: one instance handed to two services is the
// same store across a restart.
internal sealed class InMemoryChatAgentChoices : IChatAgentChoices
{
    private readonly ConcurrentDictionary<(string Household, long Chat), string> _choices = new();

    public Task<string?> GetAsync(string householdId, long chatId, CancellationToken ct) =>
        Task.FromResult(_choices.GetValueOrDefault((householdId, chatId)));

    public Task SetAsync(string householdId, long chatId, string agentId, CancellationToken ct)
    {
        _choices[(householdId, chatId)] = agentId;
        return Task.CompletedTask;
    }
}