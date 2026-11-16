using Accounts.Store;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Tests.Unit;

// A model change that no migration carries. EF asks this on every MigrateAsync and throws on a
// difference, which made every test that boots the accounts service ask it again — a model diff per
// test, the largest single cost in the suite's CPU once the routes were generated. The fixtures that
// boot a server per test copy a store migrated once and skip the question
// (AccountsServer.FromTemplate); it is asked here instead, once per store.
public class MigrationsMatchTheModelTests
{
    [Fact]
    public void TheAccountsMigrations_CarryEveryChangeToItsModel()
    {
        using var db = new AccountsDbContext(
            new DbContextOptionsBuilder<AccountsDbContext>().UseSqlite("Data Source=:memory:").Options);

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the accounts model changed without a migration: dotnet ef migrations add <Name> --project Accounts/Accounts.csproj --output-dir Store/Migrations");
    }

    [Fact]
    public void TheSugaryMigrations_CarryEveryChangeToItsModel()
    {
        using var db = new SugaryDbContext(
            new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite("Data Source=:memory:").Options);

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the Sugary model changed without a migration");
    }
}