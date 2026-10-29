using Accounts.Links;
using Accounts.Pages;
using Accounts.Passkeys;
using Accounts.Sessions;
using Accounts.Settings;
using Accounts.Store;

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
            .AddFido2(fido2 =>
            {
                fido2.RPID = settings.WebAuthn.RelyingPartyId;
                fido2.RPName = settings.WebAuthn.RelyingPartyName;
                fido2.Origins = settings.AllowedOrigins.Select(o => o.TrimEnd('/')).ToHashSet();
            });
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
            .AddSingleton<PasskeyStore>();

    public static WebApplication MapAccounts(this WebApplication app)
    {
        var accounts = app.MapGroup(PathBase);
        SessionCheck.Map(accounts);
        AccountsPages.Map(accounts);
        PasskeyEndpoints.Map(accounts);
        return app;
    }
}