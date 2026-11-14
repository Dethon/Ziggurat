using McpServerSugary.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace McpServerSugary.Store;

public static class SugaryDatabase
{
    // One registration for the server and for the import command, so both open the same file the
    // same way.
    public static IServiceCollection AddSugaryStore(this IServiceCollection services, SugarySettings settings) =>
        services
            .AddDbContextFactory<SugaryDbContext>(options => options.UseSqlite($"Data Source={settings.DatabasePath}"))
            .AddSingleton<SugaryStore>();

    // Creates the file's directory and brings the schema up to date. Run before anything reads or
    // writes; never at registration, which must not touch the disk.
    public static async Task MigrateAsync(IDbContextFactory<SugaryDbContext> contexts, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (Path.GetDirectoryName(Path.GetFullPath(db.Database.GetDbConnection().DataSource)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await db.Database.MigrateAsync(ct);
    }
}

// Hosted services start before the server takes its first request, so the mount never reads a
// database whose schema is missing.
public sealed class SugaryDatabaseMigrator(IDbContextFactory<SugaryDbContext> contexts) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        SugaryDatabase.MigrateAsync(contexts, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}