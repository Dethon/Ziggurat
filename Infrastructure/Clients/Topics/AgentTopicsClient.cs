using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;

namespace Infrastructure.Clients.Topics;

// The agent's topic-minting route, asked over a plain HttpClient whose base address and handler are
// the registration's: on the compose network today, through a server's tunnel back to the
// deployment once it has one, which swaps the handler and nothing here.
public sealed class AgentTopicsClient(HttpClient http) : ITopicMinter
{
    public const string ClientName = "agent-topics";

    public async Task<TopicMinted> MintAsync(TopicMintRequest request, string? assertion, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, TopicMinting.Path.TrimStart('/'))
        {
            Content = JsonContent.Create(request)
        };
        if (assertion is not null)
        {
            message.Headers.TryAddWithoutValidation(ForwardedAccount.AssertionHeader, assertion);
        }

        using var response = await http.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TopicMinted>(ct)
               ?? throw new HttpRequestException("The agent answered a topic mint with no body.");
    }
}