using Accounts.Links;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Commands;

// The service's verbs besides serving, run inside its container against the same store:
//
//   docker exec accounts dotnet Accounts.dll mint-link --operator --name "<display name>"
//   docker exec accounts dotnet Accounts.dll mint-link --user <id>
//   docker exec accounts dotnet Accounts.dll mint-session --user <id>
//
// The first is the operator's way in before anyone can sign in, and back in after losing every
// passkey; the second is a recovery link for anyone; the third prints a session cookie's value for
// a seeded person, so a test stack signs in without a passkey ceremony per test.
public static class AccountsCommand
{
    public const string MintLink = "mint-link";
    public const string MintSession = "mint-session";

    private const string Usage =
        $"usage: {MintLink} --operator [--name <display name>] | {MintLink} --user <id> | {MintSession} --user <id>";

    public static bool IsCommand(IReadOnlyList<string> args) => args is [MintLink or MintSession, ..];

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args, AccountsSettings settings, AccountsClock clock, TextWriter output,
        CancellationToken ct)
    {
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

    private static async Task WriteAsync(MintedLink link, TextWriter output)
    {
        await output.WriteLineAsync(
            $"A passkey link for {link.DisplayName} (user {link.UserId}, household {link.TenantId}), "
            + $"good once, until {link.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC:");
        await output.WriteLineAsync(link.Url.ToString());
    }
}