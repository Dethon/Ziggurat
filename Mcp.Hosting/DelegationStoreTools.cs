using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security.Signing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace Mcp.Hosting;

// What a server that keeps delegations answers the agent's operator commands with: every object it
// fires with the delegation it keeps and what it does now, and a replacement for one. Behind the
// call-token gate like every tool, so only the agent's own signature reaches them; never a model's,
// since the agent strips them from every tool list it builds (ThreadSession).
[McpServerToolType]
public sealed class DelegationStoreTools(IDelegationStore store)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = ChannelProtocol.ListDelegationsTool)]
    [Description("List the objects this server fires with the delegation each keeps — for the agent's operator commands")]
    public async Task<string> List(CancellationToken ct) =>
        JsonSerializer.Serialize(await store.ListAsync(ct), _json);

    // A token that does not name the object it is offered for is refused here, before it is stored:
    // the agent would refuse it at fire time anyway, and storing it would only hide the object's
    // standing until then.
    [McpServerTool(Name = ChannelProtocol.ReplaceDelegationTool)]
    [Description("Replace one object's delegation — for the agent's operator commands")]
    public async Task<string> Replace(
        [Description("The object's kind")] string kind,
        [Description("The object's id")] string id,
        [Description("The agent's delegation for it")] string token,
        CancellationToken ct) =>
        Delegation.Unverified(token) is not { } named || named.Kind != kind || named.ObjectId != id
            ? $"refused: the delegation does not name {kind} {id}"
            : await store.ReplaceAsync(kind, id, token, ct)
                ? "replaced"
                : $"refused: there is no {kind} {id}";
}

public static class DelegationStoreExtensions
{
    public static IMcpServerBuilder AddDelegationStore<TStore>(this IMcpServerBuilder builder)
        where TStore : class, IDelegationStore
    {
        builder.Services.AddSingleton<IDelegationStore, TStore>();
        return builder.WithTools<DelegationStoreTools>();
    }
}