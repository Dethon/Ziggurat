using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Agents;

// People's custom agents, by owner: the household and the person the agent API's verified assertion
// named. Every read is on a turn's path and synchronous, so they are answered from this process's
// copy; the store is the durable record, written before the copy changes and read back into it
// when the host starts. Without a store an agent lasts as long as the process.
public sealed class CustomAgentRegistry(ICustomAgentStore? store = null) : IHostedService
{
    private readonly ConcurrentDictionary<Account, ConcurrentDictionary<string, AgentDefinition>> _agentsByOwner = new();

    // Started before the channel connections, so the first turn after a restart already finds them.
    // An agent registered meanwhile is newer than the stored copy and is kept.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        var stored = await store.LoadAllAsync(cancellationToken);
        foreach (var (owner, definition) in stored)
        {
            AgentsOf(owner).TryAdd(definition.Id, definition);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task AddAsync(Account owner, AgentDefinition definition, CancellationToken ct = default)
    {
        if (store is not null)
        {
            await store.SaveAsync(owner, definition, ct);
        }

        AgentsOf(owner)[definition.Id] = definition;
    }

    public async Task<bool> RemoveAsync(Account owner, string agentId, CancellationToken ct = default)
    {
        if (!_agentsByOwner.TryGetValue(owner, out var agents) || !agents.ContainsKey(agentId))
        {
            return false;
        }

        if (store is not null)
        {
            await store.RemoveAsync(owner, agentId, ct);
        }

        return agents.TryRemove(agentId, out _);
    }

    public AgentDefinition? Find(Account owner, string agentId) =>
        _agentsByOwner.TryGetValue(owner, out var agents) ? agents.GetValueOrDefault(agentId) : null;

    public IReadOnlyList<AgentDefinition> GetByOwner(Account owner) =>
        _agentsByOwner.TryGetValue(owner, out var agents) ? agents.Values.ToList() : [];

    private ConcurrentDictionary<string, AgentDefinition> AgentsOf(Account owner) =>
        _agentsByOwner.GetOrAdd(owner, _ => new ConcurrentDictionary<string, AgentDefinition>());
}