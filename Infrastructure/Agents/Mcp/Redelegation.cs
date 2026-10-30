using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

// The agent's word on every delegation the deployment keeps, said again with the key it signs with
// now. What happens to each stored delegation depends on whether the agent can still believe it:
//
//   - one its current key signed, for the object as it is now, is left alone;
//   - one an earlier key it still publishes signed (a rotation), for the object as it is now, is
//     signed again with the same claims and no question: the agent believed it a moment ago, and
//     only a re-signed one survives retiring that key;
//   - one it cannot believe — signed with a key since lost, or for an object changed since — is
//     shown to the operator as it claims to be, beside what the object does now, and signed again
//     only on their yes;
//   - an object keeping none is offered as its server's recorded writer, when it recorded one: a
//     schedule written before delegations, stamped by the household migration. That rests on the
//     operator's yes alone, once, which is the point of asking.
//
// A delegation signed again is a fresh one over what the object does now, so a yes never blesses
// anything but what was shown.
public sealed class Redelegation(
    SigningKeyStore keys,
    CallTokens tokens,
    TimeProvider clock,
    TokenVerificationSettings verification,
    TextReader input,
    TextWriter output)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly TokenVerifier _anyKey = new(TokenIssuers.Agent, keys, clock, verification);
    private readonly TokenVerifier _currentKey = new(TokenIssuers.Agent, new CurrentKeyOnly(keys), clock, verification);

    // A server the agent dials, by the name its operator knows it by, signing its calls as nobody.
    public sealed record Server(string Name, Func<CancellationToken, Task<McpClient>> ConnectAsync);

    private sealed record Dialled(string Name, McpClient Client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    private abstract record Standing
    {
        public sealed record Current : Standing;

        public sealed record EarlierKey(Delegation Claims) : Standing;

        public sealed record NotBelieved(string Why, Delegation? Claims) : Standing;

        public sealed record None : Standing;
    }

    // Zero when every delegation the servers keep is now the current key's and every question was
    // answered; one when a server could not be reached or refused a replacement.
    public async Task<int> RedelegateAsync(IReadOnlyList<Server> servers, bool assumeYes, CancellationToken ct)
    {
        var failed = false;
        foreach (var server in servers)
        {
            await using var dialled = await DialAsync(server, ct);
            if (dialled is null || await ListAsync(dialled, ct) is not { } stored)
            {
                failed |= await UnreachableAsync(server.Name);
                continue;
            }

            foreach (var delegation in stored)
            {
                failed |= !await RedelegateAsync(dialled, delegation, assumeYes, ct);
            }
        }

        return failed ? 1 : 0;
    }

    // How many stored delegations retiring every key but the current one would strand, or null when a
    // server that may keep some could not be asked.
    public async Task<int?> StrandedByRetiringAsync(IReadOnlyList<Server> servers, CancellationToken ct)
    {
        var stranded = 0;
        foreach (var server in servers)
        {
            await using var dialled = await DialAsync(server, ct);
            if (dialled is null || await ListAsync(dialled, ct) is not { } stored)
            {
                await UnreachableAsync(server.Name);
                return null;
            }

            foreach (var delegation in stored)
            {
                if (await StandingOfAsync(delegation, ct) is Standing.EarlierKey)
                {
                    stranded++;
                    await output.WriteLineAsync($"{server.Name}: {Name(delegation)} is still signed with a key retiring would unpublish.");
                }
            }
        }

        return stranded;
    }

    private async Task<bool> RedelegateAsync(Dialled server, StoredDelegation stored, bool assumeYes, CancellationToken ct)
    {
        var what = new DelegatedObject(stored.Kind, stored.Id, stored.Description);
        switch (await StandingOfAsync(stored, ct))
        {
            case Standing.Current:
                return true;
            case Standing.EarlierKey { Claims: var claims }:
                await output.WriteLineAsync($"{server.Name}: {Name(stored)} re-signed for {DescribePerson(claims)} with the current key.");
                return await ReplaceAsync(server, stored, tokens.Delegate(PersonOf(claims), what), ct);
            case Standing.NotBelieved { Why: var why, Claims: { } claims } when claims.Account is not null:
                return !await ConfirmAsync(server, stored, $"its delegation {why}; it claims to act for {DescribePerson(claims)}", assumeYes)
                       || await ReplaceAsync(server, stored, tokens.Delegate(PersonOf(claims), what), ct);
            case Standing.None when stored.Writer is { Account: { } account } writer:
                var person = IdentityClaims.Person(account.UserId, account.TenantId, writer.ChannelId, []);
                return !await ConfirmAsync(server, stored, $"it has no delegation; it was written by {DescribePerson(person)}", assumeYes)
                       || await ReplaceAsync(server, stored, tokens.Delegate(person, what), ct);
            default:
                await output.WriteLineAsync($"{server.Name}: {Name(stored)} acts for nobody, and nothing says for whom it could.");
                return true;
        }
    }

    private async Task<Standing> StandingOfAsync(StoredDelegation stored, CancellationToken ct)
    {
        if (stored.Token is not { } token)
        {
            return new Standing.None();
        }

        var verification = await _anyKey.VerifyAsync<Delegation>(token, TokenAudiences.Delegation, ct);
        if (!verification.IsVerified)
        {
            return new Standing.NotBelieved($"does not verify ({verification.Refusal})", Delegation.Unverified(token));
        }

        var claims = verification.Claims;
        if (claims.Kind != stored.Kind || claims.ObjectId != stored.Id || claims.Digest != DelegatedObject.DigestOf(stored.Description))
        {
            return new Standing.NotBelieved("was signed for something other than what it does now", claims);
        }

        return (await _currentKey.VerifyAsync<Delegation>(token, TokenAudiences.Delegation, ct)).IsVerified
            ? new Standing.Current()
            : new Standing.EarlierKey(claims);
    }

    private async Task<bool> ConfirmAsync(Dialled server, StoredDelegation stored, string why, bool assumeYes)
    {
        await output.WriteLineAsync($"{server.Name}: {Name(stored)} — {why}.");
        await output.WriteLineAsync($"  what it does: {stored.Description}");
        if (assumeYes)
        {
            return true;
        }

        await output.WriteAsync("  re-delegate it so? [y/N] ");
        return (await input.ReadLineAsync())?.Trim().ToLowerInvariant() is "y" or "yes";
    }

    private async Task<bool> ReplaceAsync(Dialled server, StoredDelegation stored, string token, CancellationToken ct)
    {
        var answer = Text(await server.Client.CallToolAsync(ChannelProtocol.ReplaceDelegationTool,
            new Dictionary<string, object?> { ["kind"] = stored.Kind, ["id"] = stored.Id, ["token"] = token },
            cancellationToken: ct));
        if (answer == "replaced")
        {
            return true;
        }

        await output.WriteLineAsync($"{server.Name}: {Name(stored)} was not re-delegated: {answer}");
        return false;
    }

    private static async Task<Dialled?> DialAsync(Server server, CancellationToken ct)
    {
        try
        {
            return new Dialled(server.Name, await server.ConnectAsync(ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // Empty when the server keeps no delegations; null when it could not be asked.
    private static async Task<IReadOnlyList<StoredDelegation>?> ListAsync(Dialled server, CancellationToken ct)
    {
        try
        {
            if ((await server.Client.ListToolsAsync(cancellationToken: ct)).All(t => t.Name != ChannelProtocol.ListDelegationsTool))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<StoredDelegation>>(
                Text(await server.Client.CallToolAsync(ChannelProtocol.ListDelegationsTool, cancellationToken: ct)), _json) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // Said once per server: a server that cannot be reached may be keeping delegations nobody looked at.
    private async Task<bool> UnreachableAsync(string server)
    {
        await output.WriteLineAsync($"{server}: could not be asked for the delegations it keeps.");
        return true;
    }

    private static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static string Name(StoredDelegation stored) => $"{stored.Kind} {stored.Id}";

    private static string DescribePerson(IdentityClaims claims) =>
        $"user {claims.Subject} of household {claims.TenantId} (on {(claims.ChannelId.Length > 0 ? claims.ChannelId : "no channel")})";

    private static IdentityClaims PersonOf(IdentityClaims claims) =>
        IdentityClaims.Person(claims.Subject, claims.TenantId, claims.ChannelId, claims.Roles);

    // The agent's own keys narrowed to the one it signs with now: what a delegation must verify
    // against to survive the earlier ones being retired.
    private sealed class CurrentKeyOnly(SigningKeyStore keys) : IVerificationKeys
    {
        public ValueTask<VerificationKey?> FindAsync(string keyId, CancellationToken ct) =>
            keyId == keys.CurrentKeyId ? keys.FindAsync(keyId, ct) : ValueTask.FromResult<VerificationKey?>(null);
    }
}