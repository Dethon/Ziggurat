using Accounts.Links;
using Accounts.Members;
using Accounts.Pages;
using Accounts.Passkeys;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;
using Microsoft.AspNetCore.DataProtection;

namespace Accounts;

public static class AccountsApp
{
    // Every route sits under /accounts, because the proxy hands this service that path on the main
    // host as it is, and the session check is asked for at /accounts/verify.
    public const string PathBase = "/accounts";

    public static IServiceCollection AddAccounts(
        this IServiceCollection services, AccountsSettings settings, AccountsClock clock)
    {
        services
            .AddAccountsCore(settings, clock)
            .AddHostedService<AccountsDatabasePreparer>()
            .AddSingleton<CeremonyStore>()
            .AddScoped<PasskeyCeremonies>()
            .AddSingleton<PageGuard>()
            .AddSingleton<AccountPage>()
            .AddSingleton<MembersPage>()
            // Host-only and under /accounts, unlike the session cookie: it is this service's alone,
            // and a sibling host under the parent domain never receives it. Not marked Secure:
            // TLS ends at the proxy, so no request here is https, and antiforgery refuses to issue
            // an always-secure cookie on a plain one. The cookie opens nothing by itself; a form's
            // token is sealed with this service's data-protection keys.
            .AddAntiforgery(antiforgery =>
            {
                antiforgery.Cookie.Name = "ziggurat_antiforgery";
                antiforgery.Cookie.Path = PathBase;
            })
            .AddFido2(fido2 =>
            {
                fido2.RPID = settings.WebAuthn.RelyingPartyId;
                fido2.RPName = settings.WebAuthn.RelyingPartyName;
                fido2.Origins = settings.AllowedOrigins.Select(o => o.TrimEnd('/')).ToHashSet();
            });
        // Beside the store, in the volume, so a restart leaves the pages' open forms valid.
        services.AddDataProtection().PersistKeysToFileSystem(
            new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath)) ?? ".", "keys")));
        return services;
    }

    // What the commands share with the server: the store and the operations on it, without the web.
    public static IServiceCollection AddAccountsCore(
        this IServiceCollection services, AccountsSettings settings, AccountsClock clock) =>
        services
            .AddSingleton(settings)
            .AddSingleton(clock)
            .AddAccountsStore(settings)
            .AddSingleton<SessionStore>()
            .AddSingleton<LinkStore>()
            .AddSingleton<PasskeyStore>()
            .AddSingleton<MemberStore>();

    public static WebApplication MapAccounts(this WebApplication app)
    {
        var accounts = app.MapGroup(PathBase);
        SessionCheck.Map(accounts);
        AccountsPages.Map(accounts);
        AccountPage.Map(accounts);
        MembersPage.Map(accounts);
        PasskeyEndpoints.Map(accounts);
        return app;
    }
}