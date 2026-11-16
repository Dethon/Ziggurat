using System.Globalization;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;

namespace McpServerSugary.Import;

public sealed record ImportReport(int MealsAdded, int ItemsAdded, int FoodsAdded, int MealsPresent, int FoodsPresent);

// Reads the standalone app's three downloads — meals, items, foods — into this server's database
// once, keeping every identifier, time and source. A meal or a food whose id is already here, whoever
// it belongs to, is left as it is, items and all, so a second run adds nothing and never overwrites a
// meal edited since the first. The downloads name no person, so what they bring is nobody's
// (Owners.Unassigned) until the migration into the home household gives it to the person whose log
// it was, as it does every row from before the log was per person.
public static class SugaryImport
{
    public const string MealsFile = "sugary-meals.csv";
    public const string ItemsFile = "sugary-items.csv";
    public const string FoodsFile = "sugary-foods.csv";

    public static async Task<ImportReport> RunAsync(
        IDbContextFactory<SugaryDbContext> contexts, string directory, TimeProvider time, CancellationToken ct)
    {
        var meals = Table.Read(directory, MealsFile, SugaryTables.MealColumns);
        var items = Table.Read(directory, ItemsFile, SugaryTables.ItemColumns);
        var foods = Table.Read(directory, FoodsFile, SugaryTables.FoodColumns);

        var itemsByMeal = items.Rows
            .Select(row => (MealId: row.Guid("meal_id"), Item: ToItem(row)))
            .ToLookup(entry => entry.MealId, entry => entry.Item);
        var parsedMeals = meals.Rows.Select(row => ToMeal(row, itemsByMeal)).DistinctBy(meal => meal.Id).ToList();
        var importedAt = time.GetUtcNow();
        var parsedFoods = foods.Rows.Select(row => ToFood(row, importedAt)).DistinctBy(food => food.Id).ToList();

        await SugaryDatabase.MigrateAsync(contexts, ct);
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var mealIds = parsedMeals.Select(meal => meal.Id).ToList();
        var presentMeals = (await db.Meals.IgnoreQueryFilters()
                .Where(meal => mealIds.Contains(meal.Id)).Select(meal => meal.Id).ToListAsync(ct))
            .ToHashSet();
        var foodIds = parsedFoods.Select(food => food.Id).ToList();
        var presentFoods = (await db.Foods.IgnoreQueryFilters()
                .Where(food => foodIds.Contains(food.Id)).Select(food => food.Id).ToListAsync(ct))
            .ToHashSet();

        var newMeals = parsedMeals.Where(meal => !presentMeals.Contains(meal.Id)).ToList();
        var newFoods = parsedFoods.Where(food => !presentFoods.Contains(food.Id)).ToList();
        db.Meals.AddRange(newMeals);
        db.Foods.AddRange(newFoods);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new ImportReport(
            newMeals.Count, newMeals.Sum(meal => meal.Items.Count), newFoods.Count, presentMeals.Count, presentFoods.Count);
    }

    // A meal is held to the rules the app saves one under, and its total is what its items add up
    // to, as the app computes it: the row's own carbs columns are the standalone's copy of that sum.
    private static Meal ToMeal(Row row, ILookup<Guid, MealItem> itemsByMeal)
    {
        var id = row.Guid("meal_id");
        var firstBest = row.Number("first_estimate_g");
        var meal = new Meal
        {
            Id = id,
            UserId = Owners.Unassigned,
            TenantId = Owners.Unassigned,
            EatenAt = row.Time("eaten_at"),
            FirstEstimate = firstBest is { } best
                ? new Interval(row.RequiredNumber("first_estimate_low_g"), best, row.RequiredNumber("first_estimate_high_g"))
                : null,
            Outcome = row.Word<MealOutcome>("outcome"),
            Notes = row.Text("notes"),
            Items = itemsByMeal[id].OrderBy(item => item.Position).ToList()
        };
        if (MealRules.MealProblem(meal.Items, meal.FirstEstimate, meal.Notes, meal.Outcome) is { } problem)
        {
            throw row.Refused(problem);
        }

        MealRules.Total(meal);
        return meal;
    }

    private static MealItem ToItem(Row row)
    {
        var item = new MealItem(
            row.Text("name") ?? "",
            row.Text("portion"),
            row.Number("amount"),
            row.Number("carbs_per_100"),
            row.RequiredNumber("carbs_low_g"),
            row.RequiredNumber("carbs_g"),
            row.RequiredNumber("carbs_high_g"),
            row.Word<ItemSource>("source"),
            row.Text("confidence") is null ? null : row.Word<ItemConfidence>("confidence"),
            row.Bool("edited"),
            row.Text("food_id") is null ? null : row.Guid("food_id"))
        {
            Position = (int)row.RequiredNumber("position") - 1
        };
        return MealRules.ItemProblem(item) is { } problem ? throw row.Refused(problem) : item;
    }

    // The export has no creation time for a food, so it is the moment it arrived here.
    private static Food ToFood(Row row, DateTimeOffset importedAt) => new()
    {
        Id = row.Guid("food_id"),
        TenantId = Owners.Unassigned,
        Name = row.Text("name") ?? "",
        CarbsPer100g = row.Number("carbs_per_100"),
        Unit = row.Text("unit") ?? "g",
        PortionGrams = row.Number("portion_amount"),
        PortionCarbs = row.Number("portion_carbs_g"),
        Barcode = row.Text("barcode"),
        Source = row.Word<ItemSource>("source"),
        CreatedAt = importedAt
    };

    private sealed record Table(IReadOnlyList<Row> Rows)
    {
        public static Table Read(string directory, string file, IReadOnlyList<string> columns)
        {
            var path = Path.Combine(directory, file);
            if (!File.Exists(path))
            {
                throw new ImportException($"{file} is not in {directory}.");
            }

            var records = SugaryCsv.Parse(File.ReadAllText(path));
            if (records.Count == 0)
            {
                throw new ImportException($"{file} is empty.");
            }

            var header = records[0].Cells;
            var missing = columns.Where(column => !header.Contains(column)).ToList();
            if (missing.Count > 0)
            {
                throw new ImportException($"{file} has no column {string.Join(", ", missing)}.");
            }

            var index = header.Select((name, i) => (name, i)).ToDictionary(entry => entry.name, entry => entry.i);
            return new Table(records.Skip(1).Select(record => new Row(file, record, index)).ToList());
        }
    }

    // One row's cells by column name, each read the way the export wrote it. A cell that cannot be
    // read stops the import naming the file and the line.
    private sealed record Row(string File, CsvRecord Record, IReadOnlyDictionary<string, int> Index)
    {
        public string? Text(string column)
        {
            var i = Index[column];
            var cell = i < Record.Cells.Count ? Record.Cells[i] : "";
            return cell.Length == 0 ? null : SugaryCsv.Restore(cell);
        }

        public Guid Guid(string column) =>
            System.Guid.TryParse(Text(column), out var id) ? id : throw Invalid(column, "an identifier");

        public DateTimeOffset Time(string column) =>
            DateTimeOffset.TryParse(Text(column), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : throw Invalid(column, "a time");

        // Every number in the downloads is an amount, so one that is negative, or one a spreadsheet
        // writes for a failed sum (NaN, Infinity), is no number of a meal's.
        public double? Number(string column) =>
            Text(column) is not { } text ? null
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
              && double.IsFinite(number) && number >= 0 ? number
            : throw Invalid(column, "a number");

        public double RequiredNumber(string column) => Number(column) ?? throw Invalid(column, "a number");

        public bool Bool(string column) => Text(column) switch
        {
            "true" => true,
            "false" => false,
            _ => throw Invalid(column, "true or false")
        };

        public T Word<T>(string column) where T : struct, Enum =>
            Text(column) is { } word && Enum.TryParse<T>(word, ignoreCase: true, out var value)
                                     && Enum.IsDefined(value) && !char.IsDigit(word[0])
                ? value
                : throw Invalid(column, string.Join(", ", Enum.GetValues<T>().Select(SugaryTables.Word)));

        public ImportException Refused(string problem) => new($"{File} line {Record.Line}: {problem}");

        private ImportException Invalid(string column, string expected) =>
            new($"{File} line {Record.Line}: {column} is '{Text(column)}', not {expected}.");
    }
}

public sealed class ImportException(string message) : Exception(message);