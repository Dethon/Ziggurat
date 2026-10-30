using Accounts.Links;
using Accounts.People;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
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
//
// The first is the operator's way in before anyone can sign in, and back in after losing every
// passkey; the second is a recovery link for anyone; the third prints a session cookie's value for
// a seeded person, so a test stack signs in without a passkey ceremony per test; the fourth adds a
// person to the home household, who then needs a link from the second — the operator's way to add
// someone before the members page exists, and the test stack's way to seed its people. The last two
// change the key assertions are signed with, beside the running service, which notices on its next
// signature: a rotation makes a new key the signing one and keeps the previous published, and a
// retirement, once nothing the previous key signed can still be in date, unpublishes it.
public static class AccountsCommand
{
    public const string MintLink = "mint-link";
    public const string MintSession = "mint-session";
    public const string CreateUser = "create-user";
    public const string RotateSigningKey = "rotate-signing-key";
    public const string RetireSigningKeys = "retire-signing-keys";

    private const string Usage =
        $"usage: {MintLink} --operator [--name <display name>] | {MintLink} --user <id> | {MintSession} --user <id>"
        + $" | {CreateUser} --name <display name> [--role owner|member] | {RotateSigningKey} | {RetireSigningKeys}";

    public static bool IsCommand(IReadOnlyList<string> args) =>
        args is [MintLink or MintSession or CreateUser or RotateSigningKey or RetireSigningKeys, ..];

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, AccountsSettings settings, AccountsClock clock, TextWriter output,
        CancellationToken ct)
    {
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
        if (await links.MintRecoveryAsync(userId, ct) is not { } link)
        {
            await output.WriteLineAsync($"no link was minted: there is no user {userId}.");
            return 1;
        }

        await WriteAsync(link, output);
        return 0;
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
        var retiredAfter = TimeSpan.FromMinutes(settings.AssertionLifetimeMinutes) + new TokenVerificationSettings().ClockSkew;
        await output.WriteLineAsync(rotate
            ? $"Assertions are now signed with key {store.CurrentKeyId}. Still published: {string.Join(", ", before)}. "
              + $"Run {RetireSigningKeys} once the last assertion they signed has run out, {retiredAfter.TotalMinutes:0.#} minutes from now."
            : $"Published now: {store.CurrentKeyId} alone. Retired: "
              + $"{string.Join(", ", before.Where(id => id != store.CurrentKeyId).DefaultIfEmpty("none"))}.");
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