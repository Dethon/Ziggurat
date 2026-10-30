using Domain.DTOs.Channel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace McpServerSugary.Store;

// The log is meant to be read by other tools next to glucose data, so it is plain tables with
// plain columns: one row per meal, one row per item, one row per recipe and per ingredient, one row per dose, one row per insulin pen, one row per session of exercise, one row per period, one row per noted day, enums,
// times and days as readable text.
//
// Each person keeps a log of their own and the household shares its kitchen, so every row names its
// owner: the person and their household on a row of someone's log, the household on a food or a
// recipe. A context is opened for one owner (SugaryStore.For) and its query filters keep to that
// owner's rows in every read, a find, an update or a delete included, so no query has a predicate to
// forget; a row it adds is stamped with that owner on the way in.
public partial class SugaryDbContext(DbContextOptions<SugaryDbContext> options) : DbContext(options)
{
    // The accounts service's own key width (its IdLength), which a provider that sizes its strings is
    // told: today's ids are 32 hex characters and "legacy" is six, and a column as wide as the one an
    // id is issued from holds whatever that service issues later without a migration here.
    private const int OwnerIdLength = 64;

    // Whose log this context reads and writes. Null is nobody's: the factory's context, which reads
    // nothing through the filters and so must say IgnoreQueryFilters to read the whole table (the pen
    // reminders, the import), and which adds a row only when the row already names its owner.
    public Account? Owner { get; init; }

    // Read by the query filters on every query, so a context answers for its own owner although the
    // model, and the filters in it, are built once for every context.
    private string? OwnerUserId => Owner?.UserId;
    private string? OwnerTenantId => Owner?.TenantId;

    public DbSet<Meal> Meals => Set<Meal>();
    public DbSet<Food> Foods => Set<Food>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Dose> Doses => Set<Dose>();
    public DbSet<Pen> Pens => Set<Pen>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
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

        var pen = modelBuilder.Entity<Pen>();
        pen.Property(p => p.Kind).HasConversion<string>();
        pen.HasIndex(p => p.Kind);
        modelBuilder.Entity<PushSubscription>().HasKey(s => s.Endpoint);

        var exercise = modelBuilder.Entity<ExerciseSession>();
        exercise.Property(e => e.StartedAt).HasConversion(
            value => value.UtcDateTime,
            stored => new DateTimeOffset(DateTime.SpecifyKind(stored, DateTimeKind.Utc)));
        exercise.Property(e => e.EndedAt).HasConversion(
            value => value.HasValue ? value.Value.UtcDateTime : (DateTime?)null,
            stored => stored.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(stored.Value, DateTimeKind.Utc)) : null);
        exercise.HasIndex(e => e.StartedAt);
        exercise.Property(e => e.Kind).HasConversion<string>();

        // A day is stored as yyyy-MM-dd text, which sorts as days do. Each person notes their own days,
        // so a day is a note's key within its person's log.
        modelBuilder.Entity<Period>().HasIndex(p => p.Start);
        modelBuilder.Entity<DayNote>().HasKey(n => new { n.UserId, n.Day });

        Personal(meal);
        Personal(dose);
        Personal(pen);
        Personal(modelBuilder.Entity<PushSubscription>());
        Personal(exercise);
        Personal(modelBuilder.Entity<Period>());
        Personal(modelBuilder.Entity<DayNote>());
        Household(food);
        Household(recipe);
    }

    private void Personal<T>(EntityTypeBuilder<T> entity) where T : class, IPersonal
    {
        entity.Property(row => row.UserId).HasMaxLength(OwnerIdLength);
        entity.Property(row => row.TenantId).HasMaxLength(OwnerIdLength);
        entity.HasIndex(row => row.UserId);
        entity.HasQueryFilter(row => row.UserId == OwnerUserId && row.TenantId == OwnerTenantId);
    }

    private void Household<T>(EntityTypeBuilder<T> entity) where T : class, IHousehold
    {
        entity.Property(row => row.TenantId).HasMaxLength(OwnerIdLength);
        entity.HasIndex(row => row.TenantId);
        entity.HasQueryFilter(row => row.TenantId == OwnerTenantId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampOwners();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampOwners();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // A row added through a person's context is theirs, or their household's, without a route having
    // to say so. One added where nobody owns the context must already name its owner: a row that
    // belongs to nobody would be kept and never seen again.
    private void StampOwners()
    {
        foreach (var added in ChangeTracker.Entries().Where(entry => entry.State == EntityState.Added))
        {
            switch (added.Entity)
            {
                case IPersonal { UserId: "" } row:
                    var person = Owner ?? throw Unowned(added);
                    row.UserId = person.UserId;
                    row.TenantId = person.TenantId;
                    break;
                case IHousehold { TenantId: "" } row:
                    row.TenantId = (Owner ?? throw Unowned(added)).TenantId;
                    break;
            }
        }
    }

    private static InvalidOperationException Unowned(EntityEntry added) =>
        new($"A {added.Metadata.ClrType.Name} was added with nobody to own it.");
}

// What `dotnet ef migrations add` builds the model from, so the tool never has to boot the server.
public sealed class SugaryDesignTimeContextFactory : IDesignTimeDbContextFactory<SugaryDbContext>
{
    public SugaryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite("Data Source=sugary.db").Options);
}