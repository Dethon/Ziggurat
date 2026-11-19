using Accounts;
using Accounts.Members;
using Accounts.Settings;
using Accounts.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Integration.Accounts;

// "Uses Sugary" starts off for somebody new, but everyone already in a household when it arrived keeps
// the Sugary they had (user's decision, 2026-11-19): the column is added on for the rows there, and
// the store writes it off for every person made after.
public class SugaryUsersMigrationTests
{
    [Fact]
    public async Task EveryoneAlreadyThere_KeepsSugary_AndSomebodyNewStartsWithout()
    {
        var directory = Directory.CreateTempSubdirectory("sugary-users-").FullName;
        try
        {
            var clock = new AccountsClock(new FakeTimeProvider(DateTimeOffset.UnixEpoch));
            await using var services = new ServiceCollection()
                .AddAccountsCore(new AccountsSettings { DatabasePath = Path.Combine(directory, "accounts.db") }, clock)
                .BuildServiceProvider();
            var contexts = services.GetRequiredService<IDbContextFactory<AccountsDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261112213200_SugaryLogReaders");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Tenants (Id, Name, CreatedAt, IsHome) VALUES ('casa', 'Casa', '2026-11-01 00:00:00', 1)");
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO Users (Id, TenantId, DisplayName, Role, IsOperator, SeesAllConversations, ReadsSugaryLogs, CreatedAt) "
                    + "VALUES ('tradaly', 'casa', 'Tradaly', 'Member', 0, 0, 0, '2026-11-01 00:00:00')");
            }

            await AccountsDatabase.PrepareAsync(contexts, clock.Time, CancellationToken.None);
            var laura = await services.GetRequiredService<global::Accounts.People.PersonStore>()
                .CreateAsync("Laura", Role.Member, CancellationToken.None);

            var members = await services.GetRequiredService<MemberStore>().ListAsync("casa", CancellationToken.None);
            members.Single(m => m.Id == "tradaly").UsesSugary.ShouldBeTrue();
            members.Single(m => m.Id == laura.Id).UsesSugary.ShouldBeFalse();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}