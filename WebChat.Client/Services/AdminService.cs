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

    public Task<AdminAnswer<HouseholdSettings>> SaveSettingsAsync(HouseholdSettings settings, HouseholdSettings? loaded = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Settings) { Content = JsonContent.Create(AdminEdit.Of(settings, loaded)) },
            SavedAsync<HouseholdSettings>);

    public Task<AdminAnswer<HouseholdKeys>> GetKeysAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Keys), async content =>
            new AdminAnswer<HouseholdKeys>.Answered((await content.ReadFromJsonAsync<HouseholdKeys>())!));

    public Task<AdminAnswer<HouseholdKeys>> SaveOpenRouterKeyAsync(OpenRouterKey key) =>
        SaveKeysAsync(AdminProtocol.OpenRouterKey, key);

    public Task<AdminAnswer<HouseholdKeys>> SaveIdealistaKeysAsync(IdealistaKeys keys) =>
        SaveKeysAsync(AdminProtocol.IdealistaKeys, keys);

    public Task<AdminAnswer<HouseholdKeys>> SaveTelegramBotAsync(string agentId, TelegramBotKey bot) =>
        SaveKeysAsync(AdminProtocol.TelegramBot(agentId), bot);

    private Task<AdminAnswer<HouseholdKeys>> SaveKeysAsync<TKey>(string path, TKey key) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, path) { Content = JsonContent.Create(key) }, SavedAsync<HouseholdKeys>);

    public Task<AdminAnswer<AdminAgents>> GetAgentsAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Agents), async content =>
            new AdminAnswer<AdminAgents>.Answered((await content.ReadFromJsonAsync<AdminAgents>())!));

    public Task<AdminAnswer<AdminAgents>> SaveAgentAsync(string agentId, AgentEdit edit) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Agent(agentId)) { Content = JsonContent.Create(edit) }, SavedAsync<AdminAgents>);

    public Task<AdminAnswer<AdminAgents>> AddAgentAsync(AgentAddition addition) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.Agents) { Content = JsonContent.Create(addition) }, SavedAsync<AdminAgents>);

    public Task<AdminAnswer<AdminAgents>> RemoveAgentAsync(string agentId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Delete, AdminProtocol.Agent(agentId)), SavedAsync<AdminAgents>);

    public Task<AdminAnswer<AdminAgents>> SetVoiceAgentAsync(VoiceAgentChoice choice) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.VoiceAgent) { Content = JsonContent.Create(choice) }, SavedAsync<AdminAgents>);

    public Task<AdminAnswer<IReadOnlyList<AdminAgentVersion>>> GetAgentVersionsAsync(string agentId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.AgentVersions(agentId)), async content =>
            new AdminAnswer<IReadOnlyList<AdminAgentVersion>>.Answered(
                (await content.ReadFromJsonAsync<List<AdminAgentVersion>>())!));

    // A body-less POST: the restore says which version in its path alone.
    public Task<AdminAnswer<AdminAgents>> RestoreAgentVersionAsync(string agentId, string versionId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.RestoreAgentVersion(agentId, versionId)), SavedAsync<AdminAgents>);

    public Task<AdminAnswer<AdminMembers>> GetMembersAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Members), async content =>
            new AdminAnswer<AdminMembers>.Answered((await content.ReadFromJsonAsync<AdminMembers>())!));

    public Task<AdminAnswer<MemberLink>> InviteMemberAsync(MemberInvite invite) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.MemberInvites) { Content = JsonContent.Create(invite) },
            SavedAsync<MemberLink>);

    public Task<AdminAnswer<AdminMembers>> SaveMemberAsync(string userId, MemberEdit edit) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Member(userId)) { Content = JsonContent.Create(edit) },
            SavedAsync<AdminMembers>);

    // Body-less POSTs, as a restore is: the path says whom.
    public Task<AdminAnswer<AdminMembers>> DisableMemberAsync(string userId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.DisableMember(userId)), SavedAsync<AdminMembers>);

    public Task<AdminAnswer<AdminMembers>> EnableMemberAsync(string userId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.EnableMember(userId)), SavedAsync<AdminMembers>);

    public Task<AdminAnswer<MemberLink>> MintMemberRecoveryAsync(string userId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.MemberRecovery(userId)), SavedAsync<MemberLink>);

    public Task<AdminAnswer<TelegramGroupCode>> MintTelegramGroupCodeAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.MemberTelegramGroups), SavedAsync<TelegramGroupCode>);

    public Task<AdminAnswer<FeaturesSection>> GetFeaturesAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Features), async content =>
            new AdminAnswer<FeaturesSection>.Answered((await content.ReadFromJsonAsync<FeaturesSection>())!));

    public Task<AdminAnswer<FeaturesSection>> SaveFeaturesAsync(FeaturesSection features, FeaturesSection? loaded = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Features) { Content = JsonContent.Create(AdminEdit.Of(features, loaded)) },
            SavedAsync<FeaturesSection>);

    public Task<AdminAnswer<AdminSpaces>> GetSpacesAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Spaces), async content =>
            new AdminAnswer<AdminSpaces>.Answered((await content.ReadFromJsonAsync<AdminSpaces>())!));

    public Task<AdminAnswer<AdminSpaces>> SaveSpacesAsync(SpacesSection spaces, SpacesSection? loaded = null) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Put, AdminProtocol.Spaces) { Content = JsonContent.Create(AdminEdit.Of(spaces, loaded)) },
            SavedAsync<AdminSpaces>);

    public Task<AdminAnswer<AdminServers>> GetServersAsync() =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, AdminProtocol.Servers), async content =>
            new AdminAnswer<AdminServers>.Answered((await content.ReadFromJsonAsync<AdminServers>())!));

    public Task<AdminAnswer<MintedServerCredential>> AddServerAsync(ServerAddition addition) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.Servers) { Content = JsonContent.Create(addition) },
            SavedAsync<MintedServerCredential>);

    public Task<AdminAnswer<AdminServers>> RevokeServerCredentialAsync(string credentialId) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, AdminProtocol.RevokeServerCredential(credentialId)), SavedAsync<AdminServers>);

    public Task<AdminAnswer<AdminUsage>> GetUsageAsync(string? month) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get,
                month is null ? AdminProtocol.Usage : $"{AdminProtocol.Usage}?month={Uri.EscapeDataString(month)}"),
            async content => new AdminAnswer<AdminUsage>.Answered((await content.ReadFromJsonAsync<AdminUsage>())!));

    private static async Task<AdminAnswer<T>.Answered> SavedAsync<T>(HttpContent content)
    {
        var saved = (await content.ReadFromJsonAsync<AdminSaved<T>>())!;
        return new AdminAnswer<T>.Answered(saved.Value, saved.Warning);
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
                // Another owner changed the same field since the page loaded it: said, not "unreachable".
                HttpStatusCode.Conflict when await RefusalAsync(response) is { } refusal => new AdminAnswer<T>.Refused(refusal),
                HttpStatusCode.ServiceUnavailable => new AdminAnswer<T>.Unreachable((await RefusalAsync(response))?.Message),
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