using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Metrics;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;

namespace Infrastructure.Memory;

// A memory model's OpenRouter client per payer, made the first time that payer asks and kept for the
// process: each is sent with the payer's key — the deployment's where the payer names none, as for the
// home household — and counts its usage under the payer's household, so a household's extraction and
// dreaming are its own calls. Every client rides the one connection pool, so a payer's first call
// costs no handshake of its own.
public sealed class PaidChatClients(
    string apiUrl,
    string deploymentKey,
    string model,
    IMetricsPublisher metricsPublisher,
    int? maxContextTokens = null,
    ProviderRouting? providerRouting = null,
    HttpMessageHandler? transportHandler = null)
{
    private readonly ConcurrentDictionary<ModelPayer, Lazy<IChatClient>> _clients = new();

    public IChatClient For(ModelPayer payer) =>
        _clients.GetOrAdd(payer, p => new Lazy<IChatClient>(() => new OpenRouterChatClient(
            apiUrl,
            p.Key?.Reveal() ?? deploymentKey,
            model,
            maxContextTokens,
            metricsPublisher.ForHousehold(p.TenantId),
            providerRouting: providerRouting,
            transportHandler: transportHandler))).Value;
}