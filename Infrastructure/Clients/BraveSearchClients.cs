using Domain.Contracts;
using Domain.DTOs.Household;

namespace Infrastructure.Clients;

// Brave's searches on whichever household's key a call proved. The key is the household's, read per
// call, so a key an owner replaces is simply the next search's.
public sealed class BraveSearchClients(IHttpClientFactory http)
{
    public const string ClientName = nameof(IWebSearchClient);

    public IWebSearchClient For(BraveSearchKey key) => new BraveSearchClient(http.CreateClient(ClientName), key.ApiKey);
}