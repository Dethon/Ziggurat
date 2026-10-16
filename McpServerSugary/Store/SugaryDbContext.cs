using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace McpServerSugary.Store;

// The log is meant to be read by other tools next to glucose data, so it is plain tables with
// plain columns: one row per meal, one row per item, one row per dose, one row per period, one row per noted day, enums,
// times and days as readable text.
public class SugaryDbContext(DbContextOptions<SugaryDbContext> options) : DbContext(options)
{
    public DbSet<Meal> Meals => Set<Meal>();
    public DbSet<Food> Foods => Set<Food>();
    public DbSet<Dose> Doses => Set<Dose>();
    public DbSet<Period> Periods => Set<Period>();
    public DbSet<DayNote> DayNotes => Set<DayNote>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var meal = modelBuilder.Entity<Meal>();
        // SQLite has no native DateTimeOffset and EF cannot order by its text form, so the instant
        // is stored as UTC text, which sorts correctly and reads as a time.
        meal.Property(m => m.EatenAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        meal.HasIndex(m => m.EatenAt);
        meal.Property(m => m.Outcome).HasConversion<string>();
        meal.OwnsOne(m => m.FirstEstimate, estimate =>
        {
            estimate.Property(e => e.Low).HasColumnName("FirstEstimateLow");
            estimate.Property(e => e.Best).HasColumnName("FirstEstimateBest");
            estimate.Property(e => e.High).HasColumnName("FirstEstimateHigh");
        });
        meal.OwnsMany(m => m.Items, item =>
        {
            item.ToTable("MealItems");
            item.WithOwner().HasForeignKey("MealId");
            item.Property<int>("Id");
            item.HasKey("Id");
            item.HasIndex("MealId", nameof(MealItem.Position));
            item.Property(i => i.Source).HasConversion<string>();
            item.Property(i => i.Confidence).HasConversion<string>();
            item.HasIndex(i => i.FoodId);
        });

        var food = modelBuilder.Entity<Food>();
        food.Property(f => f.Source).HasConversion<string>();
        food.Property(f => f.CreatedAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        food.HasIndex(f => f.Barcode);

        var dose = modelBuilder.Entity<Dose>();
        dose.Property(d => d.TakenAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        dose.HasIndex(d => d.TakenAt);
        dose.Property(d => d.Kind).HasConversion<string>();
        dose.HasIndex(d => d.MealId);

        // A day is stored as yyyy-MM-dd text, which sorts as days do.
        modelBuilder.Entity<Period>().HasIndex(p => p.Start);
        modelBuilder.Entity<DayNote>().HasKey(n => n.Day);
    }
}

// What `dotnet ef migrations add` builds the model from, so the tool never has to boot the server.
public sealed class SugaryDesignTimeContextFactory : IDesignTimeDbContextFactory<SugaryDbContext>
{
    public SugaryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite("Data Source=sugary.db").Options);
}