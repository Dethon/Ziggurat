using System.Collections.Concurrent;
using Accounts;
using Accounts.Settings;
using Accounts.Store;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Integration.Accounts;

// A store whose schema is current is opened, not migrated again. EF's migrator builds the design-time
// model and diffs it against the last migration on every call, applied or not, so each start of a
// service and each run of an accounts command paid a model diff for nothing — the suite's largest
// in-process cost. Whether a migration was forgotten is MigrationsMatchTheModelTests' question.
public class StoreAlreadyCurrentTests
{
    [Fact]
    public async Task AnAccountsStoreAlreadyCurrent_IsNotMigratedAgain_AndKeepsItsOneHome()
    {
        var directory = Directory.CreateTempSubdirectory("store-current-").FullName;
        var migrations = new ConcurrentQueue<EventId>();
        try
        {
            var clock = new AccountsClock(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
            await using var services = new ServiceCollection()
                .AddAccountsCore(new AccountsSettings { DatabasePath = Path.Combine(directory, "accounts.db") }, clock)
                .ConfigureDbContext<AccountsDbContext>(options => options.LogTo(_ => { }, (id, _) => Record(migrations, id)))
                .BuildServiceProvider();
            var contexts = services.GetRequiredService<IDbContextFactory<AccountsDbContext>>();

            await AccountsDatabase.PrepareAsync(contexts, clock.Time, CancellationToken.None);
            migrations.ShouldNotBeEmpty("a new store is migrated");
            migrations.Clear();

            await AccountsDatabase.PrepareAsync(contexts, clock.Time, CancellationToken.None);

            migrations.ShouldBeEmpty();
            await using var db = await contexts.CreateDbContextAsync();
            (await db.Tenants.CountAsync(t => t.IsHome)).ShouldBe(1);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ASugaryStoreAlreadyCurrent_IsNotMigratedAgain()
    {
        var directory = Directory.CreateTempSubdirectory("store-current-").FullName;
        var migrations = new ConcurrentQueue<EventId>();
        try
        {
            var path = Path.Combine(directory, "sugary.db");
            await using var services = new ServiceCollection()
                .AddDbContextFactory<SugaryDbContext>(options => options
                    .UseSqlite($"Data Source={path}")
                    .LogTo(_ => { }, (id, _) => Record(migrations, id)))
                .BuildServiceProvider();
            var contexts = services.GetRequiredService<IDbContextFactory<SugaryDbContext>>();

            await SugaryDatabase.MigrateAsync(contexts, CancellationToken.None);
            migrations.ShouldNotBeEmpty("a new store is migrated");
            migrations.Clear();

            await SugaryDatabase.MigrateAsync(contexts, CancellationToken.None);

            migrations.ShouldBeEmpty();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    // The migrator says it is running before it reads the history or builds a model.
    private static bool Record(ConcurrentQueue<EventId> migrations, EventId id)
    {
        if (id.Id == RelationalEventId.MigrateUsingConnection.Id)
        {
            migrations.Enqueue(id);
        }

        return false;
    }
}