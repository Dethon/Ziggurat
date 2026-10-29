using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;

namespace Infrastructure.Clients.Accounts;

// The accounts service's internal resolution API, asked with the caller's own MCP credential. The
// service spells its request and answer for itself (it references no project here), so the wire
// shape is these two records and the camelCase the web defaults give both ends.
public sealed class AccountsResolverClient(HttpClient http, string credential) : IAccountResolver
{
    public const string ClientName = "accounts";

    private const string ResolvePath = "accounts/internal/resolve";

    public async Task<ResolvedAccount?> ResolveAsync(string channel, string identity, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ResolvePath)
        {
            Content = JsonContent.Create(new Query(channel, identity))
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<Answer>(ct)
                     ?? throw new HttpRequestException("The accounts service answered a resolution with no body.");
        return new ResolvedAccount(new Account(answer.TenantId, answer.UserId), answer.DisplayName);
    }

    private sealed record Query(string Channel, string Identity);

    private sealed record Answer(string TenantId, string UserId, string DisplayName);
}