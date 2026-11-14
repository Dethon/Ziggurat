using Accounts.Configuration;
using Accounts.Links;
using Accounts.People;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
using Accounts.Tunnels;
using Domain.Security.Signing;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Commands;

// The service's verbs besides serving, run inside its container against the same store:
//
//   docker exec accounts dotnet Accounts.dll mint-link --operator --name "<display name>"
//   docker exec accounts dotnet Accounts.dll mint-link --user <id>
//   docker exec accounts dotnet Accounts.dll mint-session --user <id>
//   docker exec accounts dotnet Accounts.dll create-user --name "<display name>" [--role owner|member]
//   docker exec accounts dotnet Accounts.dll rotate-signing-key
//   docker exec accounts dotnet Accounts.dll retire-signing-keys
//   docker exec accounts dotnet Accounts.dll mint-tunnel-credential --household <id> --server <name>
//   docker exec accounts dotnet Accounts.dll list-tunnel-credentials
//   docker exec accounts dotnet Accounts.dll revoke-tunnel-credential --id <id>
//   docker exec accounts dotnet Accounts.dll get-household-config --household <id|home> --section <name>
//   docker exec -i accounts dotnet Accounts.dll set-household-config --household <id|home> --section <name> --file - < section.json
//   docker exec -i accounts dotnet Accounts.dll rewrap-household-keys < previous-master.key
//
// The first is the operator's way in before anyone can sign in, and back in after losing every
// passkey; the second is a recovery link for anyone; the third prints a session cookie's value for
// a seeded person, so a test stack signs in without a passkey ceremony per test; the fourth adds a
// person to the home household, who then needs a link from the second — the operator's way to add
// someone before the members page exists, and the test stack's way to seed its people. The last two
// change the key assertions are signed with, beside the running service, which notices on its next
// signature: a rotation makes a new key the signing one and keeps the previous published, and a
// retirement, once nothing the previous key signed can still be in date, unpublishes it. The tunnel
// verbs are the operator's for a household's own servers until the owner's admin area exists: a
// credential for one of a household's servers by name, printed once; every credential with no
// secret; and a revocation, which the relay is told of so it closes that credential's tunnel. The
// household configuration verbs are the operator's until the admin area exists: a section printed
// with every secret masked, a section saved from a file (or the input) with its secrets sealed on the
// way in, and the data keys rewrapped after the master key changed (.claude/rules/household-config.md).
public static class AccountsCommand
{
    public const string MintLink = "mint-link";
    public const string MintSession = "mint-session";
    public const string CreateUser = "create-user";
    public const string RotateSigningKey = "rotate-signing-key";
    public const string RetireSigningKeys = "retire-signing-keys";
    public const string MintTunnelCredential = "mint-tunnel-credential";
    public const string ListTunnelCredentials = "list-tunnel-credentials";
    public const string RevokeTunnelCredential = "revoke-tunnel-credential";
    public const string GetHouseholdConfig = "get-household-config";
    public const string SetHouseholdConfig = "set-household-config";
    public const string RewrapHouseholdKeys = "rewrap-household-keys";

    // Asking for the verbs is a command too, so the operator typing it beside the running service is
    // answered rather than starting a second server on its port.
    private static readonly string[] _help = ["--help", "-h", "help"];

    private const string Usage =
        $"usage: {MintLink} --operator [--name <display name>] | {MintLink} --user <id> | {MintSession} --user <id>"
        + $" | {CreateUser} --name <display name> [--role owner|member] | {RotateSigningKey} | {RetireSigningKeys}"
        + $" | {MintTunnelCredential} --household <id> --server <name> | {ListTunnelCredentials}"
        + $" | {RevokeTunnelCredential} --id <id>"
        + $" | {GetHouseholdConfig} --household <id|home> --section <name> | {SetHouseholdConfig} --household <id|home> --section <name> --file <path|->"
        + $" | {RewrapHouseholdKeys} (the previous master key on the input)";

    public static bool IsCommand(IReadOnlyList<string> args) =>
        args is [MintLink or MintSession or CreateUser or RotateSigningKey or RetireSigningKeys
            or MintTunnelCredential or ListTunnelCredentials or RevokeTunnelCredential
            or GetHouseholdConfig or SetHouseholdConfig or RewrapHouseholdKeys, ..]
        || args is [var only] && _help.Contains(only);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, AccountsSettings settings, AccountsClock clock, TextReader input, TextWriter output,
        CancellationToken ct)
    {
        if (args is [var asked] && _help.Contains(asked))
        {
            await output.WriteLineAsync(Usage);
            return 0;
        }

        // The keys are files beside the store, not rows in it: nothing here needs the database.
        switch (args)
        {
            case [RotateSigningKey]:
                return await SigningKeysAsync(settings, rotate: true, output);
            case [RetireSigningKeys]:
                return await SigningKeysAsync(settings, rotate: false, output);
        }

        await using var services = new ServiceCollection().AddAccountsCore(settings, clock).BuildServiceProvider();
        await AccountsDatabase.PrepareAsync(
            services.GetRequiredService<IDbContextFactory<AccountsDbContext>>(), clock.Time, ct);
        var links = services.GetRequiredService<LinkStore>();

        switch (args)
        {
            case [MintLink, "--operator"]:
                return await OperatorAsync(links, null, output, ct);
            case [MintLink, "--operator", "--name", var name]:
                return await OperatorAsync(links, name, output, ct);
            case [MintLink, "--name", var name, "--operator"]:
                return await OperatorAsync(links, name, output, ct);
            case [MintLink, "--user", var userId]:
                return await RecoveryAsync(links, userId, output, ct);
            case [MintSession, "--user", var userId]:
                return await SessionAsync(services.GetRequiredService<SessionStore>(), userId, output, ct);
            case [CreateUser, "--name", var name]:
                return await PersonAsync(services.GetRequiredService<PersonStore>(), name, Role.Member, output, ct);
            case [CreateUser, "--name", var name, "--role", var role] when RoleNamed(role) is { } named:
                return await PersonAsync(services.GetRequiredService<PersonStore>(), name, named, output, ct);
            case [MintTunnelCredential, "--household", var tenantId, "--server", var serverName]:
                return await MintTunnelAsync(services.GetRequiredService<TunnelCredentialStore>(), tenantId, serverName, output, ct);
            case [ListTunnelCredentials]:
                return await ListTunnelsAsync(services.GetRequiredService<TunnelCredentialStore>(), output, ct);
            case [RevokeTunnelCredential, "--id", var id]:
                return await RevokeTunnelAsync(services.GetRequiredService<TunnelCredentialStore>(), id, output, ct);
            case [GetHouseholdConfig, "--household", var household, "--section", var section]:
                return await HouseholdConfigCommands.GetAsync(
                    services.GetRequiredService<HouseholdConfigStore>(), household, section, output, ct);
            case [SetHouseholdConfig, "--household", var household, "--section", var section, "--file", var file]:
                return await HouseholdConfigCommands.SetAsync(
                    services.GetRequiredService<HouseholdConfigStore>(), household, section, file, input, output, ct);
            case [RewrapHouseholdKeys]:
                return await HouseholdConfigCommands.RewrapAsync(
                    services.GetRequiredService<HouseholdConfigStore>(), input, output, ct);
            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static async Task<int> OperatorAsync(LinkStore links, string? name, TextWriter output, CancellationToken ct)
    {
        try
        {
            var (link, created) = await links.MintForOperatorAsync(name, ct);
            if (created)
            {
                await output.WriteLineAsync($"Created the operator, {link.DisplayName}, as an owner of the home household.");
            }

            await WriteAsync(link, output);
            return 0;
        }
        catch (ArgumentException ex)
        {
            await output.WriteLineAsync($"no link was minted: {ex.Message}. {Usage}");
            return 2;
        }
    }

    private static async Task<int> RecoveryAsync(LinkStore links, string userId, TextWriter output, CancellationToken ct)
    {
        switch (await links.MintRecoveryAsync(userId, ct))
        {
            case RecoveryMint.Minted { Link: var link }:
                await WriteAsync(link, output);
                return 0;
            case RecoveryMint.Disabled:
                await output.WriteLineAsync(
                    $"no link was minted: user {userId} is disabled, so it would open nothing until an owner re-enables them.");
                return 1;
            default:
                await output.WriteLineAsync($"no link was minted: there is no user {userId}.");
                return 1;
        }
    }

    private static async Task<int> SessionAsync(SessionStore sessions, string userId, TextWriter output, CancellationToken ct)
    {
        if (await sessions.IssueAsync(userId, credentialId: null, device: MintSession, ct) is not { } session)
        {
            await output.WriteLineAsync($"no session was minted: there is no user {userId}.");
            return 1;
        }

        // The value alone, so a script can read it straight into a cookie.
        await output.WriteLineAsync(session.Token);
        return 0;
    }

    private static async Task<int> PersonAsync(
        PersonStore people, string name, Role role, TextWriter output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            await output.WriteLineAsync($"nobody was created: a person needs a display name. {Usage}");
            return 2;
        }

        var person = await people.CreateAsync(name, role, ct);
        await output.WriteLineAsync(
            $"Created {person.DisplayName} (user {person.Id}, household {person.TenantId}) as "
            + $"{(role == Role.Owner ? "an owner" : "a member")} of the home household. "
            + $"A passkey link for them: {MintLink} --user {person.Id}");
        return 0;
    }

    private static async Task<int> SigningKeysAsync(AccountsSettings settings, bool rotate, TextWriter output)
    {
        var keys = settings.SigningKeys;
        if (keys.PrivateKey.Length > 0)
        {
            await output.WriteLineAsync(
                "nothing was changed: assertions are signed with the one key SIGNING__PRIVATEKEY provides, "
                + "which is neither rotated nor retired here.");
            return 1;
        }

        var previous = SigningKeyStore.Open(keys);
        var before = previous.PublishedKeys.Keys.Select(k => k.KeyId).ToList();
        if (rotate)
        {
            previous.Rotate();
        }
        else
        {
            previous.Retire();
        }

        var store = SigningKeyStore.Open(keys);
        var retiredAfter = settings.AssertionLifetime + settings.Mcp.TokenVerification.ClockSkew;
        await output.WriteLineAsync(rotate
            ? $"Assertions are now signed with key {store.CurrentKeyId}. Still published: {string.Join(", ", before)}. "
              + $"Run {RetireSigningKeys} once the last assertion they signed has run out, {retiredAfter.TotalMinutes:0.#} minutes from now."
            : $"Published now: {store.CurrentKeyId} alone. Retired: "
              + $"{string.Join(", ", before.Where(id => id != store.CurrentKeyId).DefaultIfEmpty("none"))}.");
        return 0;
    }

    private static async Task<int> MintTunnelAsync(
        TunnelCredentialStore credentials, string tenantId, string serverName, TextWriter output, CancellationToken ct)
    {
        try
        {
            if (await credentials.MintAsync(tenantId, serverName, ct) is not { } minted)
            {
                await output.WriteLineAsync($"no credential was minted: there is no household {tenantId}.");
                return 1;
            }

            if (minted.NotTold is { } why)
            {
                await output.WriteLineAsync(
                    $"The agent was not told the household set {minted.ServerName} up ({why}): it reaches it within a minute.");
            }

            // The credential last and alone on its line, so a script can read it straight into the
            // server's environment; it is never shown again.
            await output.WriteLineAsync(
                $"A tunnel credential for server {minted.ServerName} of household {minted.TenantId} "
                + $"(credential {minted.Id}), shown this once:");
            await output.WriteLineAsync(minted.Credential);
            return 0;
        }
        catch (ArgumentException ex)
        {
            await output.WriteLineAsync($"no credential was minted: {ex.Message}. {Usage}");
            return 2;
        }
    }

    private static async Task<int> ListTunnelsAsync(TunnelCredentialStore credentials, TextWriter output, CancellationToken ct)
    {
        var listed = await credentials.ListAsync(ct);
        if (listed.Count == 0)
        {
            await output.WriteLineAsync("No tunnel credential has been minted.");
            return 0;
        }

        await output.WriteLineAsync(string.Join(Environment.NewLine, listed.Select(c =>
            $"credential {c.Id}, server {c.ServerName} of household {c.TenantId}, minted {c.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC, "
            + (c.RevokedAt is { } revoked ? $"revoked {revoked.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "live"))));
        return 0;
    }

    // Not telling the listeners is a failure even though the revocation stands: the operator asked
    // for the tunnel closed now, and it will close only when it next opens.
    private static async Task<int> RevokeTunnelAsync(
        TunnelCredentialStore credentials, string id, TextWriter output, CancellationToken ct)
    {
        if (await credentials.RevokeAsync(id, ct) is not { } revoked)
        {
            await output.WriteLineAsync($"nothing was revoked: there is no tunnel credential {id}.");
            return 1;
        }

        var what = $"tunnel credential {revoked.Id} (server {revoked.ServerName} of household {revoked.TenantId})";
        await output.WriteLineAsync(revoked.AlreadyRevoked
            ? $"The {what} was revoked already, on {revoked.RevokedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC."
            : $"Revoked the {what}.");
        if (revoked.NotTold is { } why)
        {
            await output.WriteLineAsync(
                $"The relay was not told: {why}. A tunnel it opened stays open until it next opens, which "
                + $"is refused; run {RevokeTunnelCredential} again to tell it.");
            return 1;
        }

        await output.WriteLineAsync("The relay was told, and closes a tunnel it opened.");
        if (revoked.ReadersNotTold is { } readers)
        {
            await output.WriteLineAsync(
                $"The agent was not told ({readers}): it stops naming {revoked.ServerName} within a minute.");
        }

        return 0;
    }

    // The household roles alone: the operator is the one mint-link --operator makes.
    private static Role? RoleNamed(string role) => role switch
    {
        Roles.Owner => Role.Owner,
        Roles.Member => Role.Member,
        _ => null
    };

    private static async Task WriteAsync(MintedLink link, TextWriter output)
    {
        await output.WriteLineAsync(
            $"A passkey link for {link.DisplayName} (user {link.UserId}, household {link.TenantId}), "
            + $"good once, until {link.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC:");
        await output.WriteLineAsync(link.Url.ToString());
    }
}