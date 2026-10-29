using Accounts.Settings;
using Microsoft.EntityFrameworkCore;

namespace Accounts.Store;

public static class AccountsDatabase
{
    public const string HomeTenantName = "Home";

    // One registration for the service and for its commands, so both open the same file the same way.
    public static IServiceCollection AddAccountsStore(this IServiceCollection services, AccountsSettings settings) =>
        services.AddDbContextFactory<AccountsDbContext>(options =>
            options.UseSqlite($"Data Source={settings.DatabasePath}"));

    // Creates the file's directory, brings the schema up to date and seeds the home household. Run
    // before anything reads or writes; never at registration, which must not touch the disk.
    public static async Task PrepareAsync(
        IDbContextFactory<AccountsDbContext> contexts, TimeProvider clock, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (Path.GetDirectoryName(Path.GetFullPath(db.Database.GetDbConnection().DataSource)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await db.Database.MigrateAsync(ct);
        if (!await db.Tenants.AnyAsync(t => t.IsHome, ct))
        {
            db.Tenants.Add(new Tenant
            {
                Id = Tokens.NewId(), Name = HomeTenantName, IsHome = true, CreatedAt = clock.GetUtcNow()
            });
            await db.SaveChangesAsync(ct);
        }
    }
}

// Hosted services start before the server takes its first request, so nothing is answered from a
// database whose schema is missing.
public sealed class AccountsDatabasePreparer(IDbContextFactory<AccountsDbContext> contexts, AccountsClock clock)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        AccountsDatabase.PrepareAsync(contexts, clock.Time, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}