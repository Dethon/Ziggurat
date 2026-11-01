using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Household;

namespace Infrastructure.Clients;

// Idealista's searches on whichever household's account a call proved: one client per key pair, so
// each account's OAuth token is asked once and kept for its own searches, and a household's new keys
// are a new client rather than a token signed for the old ones.
public sealed class IdealistaClients(IHttpClientFactory http)
{
    public const string ClientName = "idealista";

    private readonly ConcurrentDictionary<IdealistaKeys, IdealistaClient> _clients = new();

    public IIdealistaClient For(IdealistaKeys keys) =>
        _clients.GetOrAdd(keys, account => new IdealistaClient(() => http.CreateClient(ClientName), account.ApiKey, account.Secret));
}