using Accounts.Store;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Tests.Unit;

// A model change that no migration carries. EF's migrator asks this on every MigrateAsync, applied or
// not, so the services migrate only when a migration is pending (AccountsDatabase, SugaryDatabase) and
// a start on a current store never asks: it is asked here instead, once per store.
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