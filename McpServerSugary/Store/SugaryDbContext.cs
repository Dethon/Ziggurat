using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace McpServerSugary.Store;

// The log is meant to be read by other tools next to glucose data, so it is plain tables with
// plain columns: one row per meal, one row per item, one row per recipe and per ingredient, one row per dose, one row per session of exercise, one row per period, one row per noted day, enums,
// times and days as readable text.
public class SugaryDbContext(DbContextOptions<SugaryDbContext> options) : DbContext(options)
{
    public DbSet<Meal> Meals => Set<Meal>();
    public DbSet<Food> Foods => Set<Food>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Dose> Doses => Set<Dose>();
    public DbSet<ExerciseSession> ExerciseSessions => Set<ExerciseSession>();
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
            item.HasIndex(i => i.RecipeId);
        });

        var food = modelBuilder.Entity<Food>();
        food.Property(f => f.Source).HasConversion<string>();
        food.Property(f => f.CreatedAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        food.HasIndex(f => f.Barcode);

        var recipe = modelBuilder.Entity<Recipe>();
        recipe.Property(r => r.CreatedAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        recipe.OwnsMany(r => r.Ingredients, ingredient =>
        {
            ingredient.ToTable("RecipeIngredients");
            ingredient.WithOwner().HasForeignKey("RecipeId");
            ingredient.Property<int>("Id");
            ingredient.HasKey("Id");
            ingredient.HasIndex("RecipeId", nameof(RecipeIngredient.Position));
            // A food a recipe uses cannot go: the recipe would quietly lose its carbs.
            ingredient.HasOne<Food>().WithMany().HasForeignKey(i => i.FoodId).OnDelete(DeleteBehavior.Restrict);
        });

        var dose = modelBuilder.Entity<Dose>();
        dose.Property(d => d.TakenAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        dose.HasIndex(d => d.TakenAt);
        dose.Property(d => d.Kind).HasConversion<string>();

        var exercise = modelBuilder.Entity<ExerciseSession>();
        exercise.Property(e => e.StartedAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        exercise.Property(e => e.EndedAt).HasConversion(
            value => value.HasValue ? value.Value.UtcDateTime : (DateTime?)null,
            stored => stored.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(stored.Value, DateTimeKind.Utc)) : null);
        exercise.HasIndex(e => e.StartedAt);
        exercise.Property(e => e.Kind).HasConversion<string>();

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