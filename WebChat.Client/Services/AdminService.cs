using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.DTOs.Admin;
using Domain.DTOs.Household;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services;

// The admin API is on this page's own host, under /accounts/admin, which the proxy puts behind its
// session check for the admin channel: the session cookie rides along and the check signs who this
// is for the accounts service. Every request carries the admin client's header, which a page on any
// other origin could not send without a preflight nothing answers.
public sealed class AdminService(HttpClient httpClient) : IAdminService
{
    private const string NotAllowedMessage = "Only the household's owners manage it.";

    public Task<AdminAnswer<HouseholdSettings>> GetSettingsAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Settings), async content =>
            new AdminAnswer<HouseholdSettings>.Answered((await content.ReadFromJsonAsync<HouseholdSettings>())!));

    public Task<AdminAnswer<HouseholdSettings>> SaveSettingsAsync(HouseholdSettings settings) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Settings) { Content = JsonContent.Create(settings) },
            async content =>
            {
                var saved = (await content.ReadFromJsonAsync<AdminSaved<HouseholdSettings>>())!;
                return new AdminAnswer<HouseholdSettings>.Answered(saved.Value, saved.Warning);
            });

    public Task<AdminAnswer<AdminAgents>> GetAgentsAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Agents), async content =>
            new AdminAnswer<AdminAgents>.Answered((await content.ReadFromJsonAsync<AdminAgents>())!));

    public Task<AdminAnswer<AdminAgents>> SaveAgentAsync(string agentId, AgentEdit edit) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Agent(agentId)) { Content = JsonContent.Create(edit) }, SavedAgentsAsync);

    public Task<AdminAnswer<AdminAgents>> AddAgentAsync(AgentAddition addition) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.Agents) { Content = JsonContent.Create(addition) }, SavedAgentsAsync);

    public Task<AdminAnswer<AdminAgents>> RemoveAgentAsync(string agentId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Delete, AdminProtocol.Agent(agentId)), SavedAgentsAsync);

    public Task<AdminAnswer<IReadOnlyList<AdminAgentVersion>>> GetAgentVersionsAsync(string agentId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.AgentVersions(agentId)), async content =>
            new AdminAnswer<IReadOnlyList<AdminAgentVersion>>.Answered(
                (await content.ReadFromJsonAsync<List<AdminAgentVersion>>())!));

    // A body-less POST: the restore says which version in its path alone.
    public Task<AdminAnswer<AdminAgents>> RestoreAgentVersionAsync(string agentId, string versionId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.RestoreAgentVersion(agentId, versionId)), SavedAgentsAsync);

    private static async Task<AdminAnswer<AdminAgents>.Answered> SavedAgentsAsync(HttpContent content)
    {
        var saved = (await content.ReadFromJsonAsync<AdminSaved<AdminAgents>>())!;
        return new AdminAnswer<AdminAgents>.Answered(saved.Value, saved.Warning);
    }

    private async Task<AdminAnswer<T>> SendAsync<T>(
        HttpRequestMessage request, Func<HttpContent, Task<AdminAnswer<T>.Answered>> read)
    {
        request.Headers.Add(AdminProtocol.RequestHeader, AdminProtocol.RequestHeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await httpClient.SendAsync(request);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await read(response.Content),
                HttpStatusCode.Unauthorized => new AdminAnswer<T>.SignedOut(),
                HttpStatusCode.Forbidden => new AdminAnswer<T>.NotAllowed((await RefusalAsync(response))?.Message ?? NotAllowedMessage),
                HttpStatusCode.BadRequest when await RefusalAsync(response) is { } refusal => new AdminAnswer<T>.Refused(refusal),
                _ => new AdminAnswer<T>.Unreachable()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new AdminAnswer<T>.Unreachable();
        }
    }

    private static async Task<AdminRefusal?> RefusalAsync(HttpResponseMessage response)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<AdminRefusal>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}