using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// The CSV every reader of her log has been getting — the app's export and what an import reads back
// — pinned as literals, so the tables can be retyped underneath without a byte of it moving.
public class SugaryTablesTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static readonly Guid _paella = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _toast = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _rice = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly LoggedMeal[] _meals =
    [
        new(_paella, DateTimeOffset.Parse("2026-08-09T12:30:00Z"), new CarbRange(42, 70.25, 112.5),
            new CarbRange(30, 40, 55), MealOutcome.High, "ate out, \"menú\"",
            [
                new LoggedItem(1, "=prawns", null, null, null, new CarbRange(0, 0, 1), ItemSource.Manual, null,
                    true, null),
                new LoggedItem(0, "rice", "a plate", 250, 28.333, new CarbRange(42, 70.25, 111.5),
                    ItemSource.Estimate, ItemConfidence.Medium, false, _rice)
            ]),
        // Winter: the offset in Madrid is +01:00, not the summer's +02:00.
        new(_toast, DateTimeOffset.Parse("2026-01-03T07:00:00Z"), new CarbRange(25, 30, 35), null,
            MealOutcome.InRange, null,
            [new LoggedItem(0, "toast", null, 60, 50, new CarbRange(25, 30, 35), ItemSource.Label, null, false, null)])
    ];

    [Fact]
    public void Meals_AreTheCsvTheyHaveAlwaysBeen() =>
        SugaryCsv.Write(SugaryTables.Meals(_meals, _madrid)).ShouldBe(""""
            meal_id,eaten_at,carbs_g,carbs_low_g,carbs_high_g,first_estimate_g,first_estimate_low_g,first_estimate_high_g,outcome,items,notes
            22222222-2222-2222-2222-222222222222,2026-01-03T08:00:00+01:00,30,25,35,,,,inRange,toast,
            11111111-1111-1111-1111-111111111111,2026-08-09T14:30:00+02:00,70.25,42,112.5,40,30,55,high,rice; =prawns,"ate out, ""menú"""

            """");

    [Fact]
    public void Items_AreTheCsvTheyHaveAlwaysBeen() =>
        SugaryCsv.Write(SugaryTables.Items(_meals, _madrid)).ShouldBe(""""
            meal_id,eaten_at,position,name,portion,amount,carbs_per_100,carbs_g,carbs_low_g,carbs_high_g,source,confidence,edited,food_id
            22222222-2222-2222-2222-222222222222,2026-01-03T08:00:00+01:00,1,toast,,60,50,30,25,35,label,,false,
            11111111-1111-1111-1111-111111111111,2026-08-09T14:30:00+02:00,1,rice,a plate,250,28.33,70.25,42,111.5,estimate,medium,false,33333333-3333-3333-3333-333333333333
            11111111-1111-1111-1111-111111111111,2026-08-09T14:30:00+02:00,2,'=prawns,,,,0,0,1,manual,,true,

            """");

    [Fact]
    public void Doses_AreTheCsvTheyHaveAlwaysBeen() =>
        SugaryCsv.Write(SugaryTables.Doses(
        [
            new LoggedDose(Guid.Parse("44444444-4444-4444-4444-444444444444"),
                DateTimeOffset.Parse("2026-08-09T12:25:00Z"), 7, DoseKind.Rapid, [_paella], "+1 for the wine"),
            new LoggedDose(Guid.Parse("55555555-5555-5555-5555-555555555555"),
                DateTimeOffset.Parse("2026-08-08T21:00:00Z"), 14.5, DoseKind.Basal, [], null)
        ], _madrid)).ShouldBe(""""
            dose_id,taken_at,kind,units,note
            55555555-5555-5555-5555-555555555555,2026-08-08T23:00:00+02:00,basal,14.5,
            44444444-4444-4444-4444-444444444444,2026-08-09T14:25:00+02:00,rapid,7,'+1 for the wine

            """");

    // One bolus for the paella and the flan after it is two rows here and still one dose in doses,
    // so a sum of units counts it once.
    [Fact]
    public void DoseMeals_AreARowPerMealADoseWasFor_InTheDosesOrder() =>
        SugaryCsv.Write(SugaryTables.DoseMeals(
        [
            new LoggedDose(Guid.Parse("44444444-4444-4444-4444-444444444444"),
                DateTimeOffset.Parse("2026-08-09T12:25:00Z"), 7, DoseKind.Rapid,
                [_paella, Guid.Parse("99999999-9999-9999-9999-999999999999")], null),
            new LoggedDose(Guid.Parse("55555555-5555-5555-5555-555555555555"),
                DateTimeOffset.Parse("2026-08-08T21:00:00Z"), 14.5, DoseKind.Basal, [], null),
            new LoggedDose(Guid.Parse("66666666-6666-6666-6666-666666666666"),
                DateTimeOffset.Parse("2026-08-08T08:00:00Z"), 3, DoseKind.Rapid,
                [Guid.Parse("22222222-2222-2222-2222-222222222222")], null)
        ])).ShouldBe(""""
            dose_id,meal_id
            66666666-6666-6666-6666-666666666666,22222222-2222-2222-2222-222222222222
            44444444-4444-4444-4444-444444444444,11111111-1111-1111-1111-111111111111
            44444444-4444-4444-4444-444444444444,99999999-9999-9999-9999-999999999999

            """");

    [Fact]
    public void Exercise_IsTheCsvItHasAlwaysBeen() =>
        SugaryCsv.Write(SugaryTables.Exercise(
        [
            new LoggedExercise(Guid.Parse("66666666-6666-6666-6666-666666666666"), ExerciseKind.Other, "bici, rápida",
                DateTimeOffset.Parse("2026-08-09T17:00:00Z"), DateTimeOffset.Parse("2026-08-09T17:44:31Z"), null),
            new LoggedExercise(Guid.Parse("77777777-7777-7777-7777-777777777777"), ExerciseKind.Walk, null,
                DateTimeOffset.Parse("2026-08-10T08:00:00Z"), null, "still going")
        ], _madrid)).ShouldBe(""""
            exercise_id,started_at,ended_at,minutes,kind,name,note
            66666666-6666-6666-6666-666666666666,2026-08-09T19:00:00+02:00,2026-08-09T19:44:31+02:00,45,other,"bici, rápida",
            77777777-7777-7777-7777-777777777777,2026-08-10T10:00:00+02:00,,,walk,,still going

            """");

    [Fact]
    public void Periods_AreTheCsvTheyHaveAlwaysBeen() =>
        SugaryCsv.Write(SugaryTables.Periods(
        [
            new LoggedPeriod(Guid.Parse("88888888-8888-8888-8888-888888888888"), new DateOnly(2026, 8, 1), null),
            new LoggedPeriod(Guid.Parse("99999999-9999-9999-9999-999999999999"), new DateOnly(2026, 7, 4),
                new DateOnly(2026, 7, 8))
        ])).ShouldBe(""""
            period_id,start_date,end_date,days,cycle_days
            99999999-9999-9999-9999-999999999999,2026-07-04,2026-07-08,5,28
            88888888-8888-8888-8888-888888888888,2026-08-01,,,

            """");

    [Fact]
    public void DayNotes_AreTheCsvTheyHaveAlwaysBeen() =>
        SugaryCsv.Write(SugaryTables.DayNotes(
        [
            new LoggedDayNote(new DateOnly(2026, 8, 9), "-tired\nslept badly"),
            new LoggedDayNote(new DateOnly(2026, 8, 1), "fine")
        ])).ShouldBe(""""
            date,note
            2026-08-01,fine
            2026-08-09,"'-tired
            slept badly"

            """");

    [Fact]
    public void Foods_AreTheCsvTheyHaveAlwaysBeen() =>
        SugaryCsv.Write(SugaryTables.Foods(
        [
            new SavedFood(_rice, "rice", 28.333, "g", 250, null, null, ItemSource.Estimate),
            new SavedFood(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "Biscuits", null, "g", 2, 14.6,
                "8410000000000", ItemSource.Barcode)
        ])).ShouldBe(""""
            food_id,name,carbs_per_100,unit,portion_amount,portion_carbs_g,barcode,source
            aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa,Biscuits,,g,2,14.6,8410000000000,barcode
            33333333-3333-3333-3333-333333333333,rice,28.33,g,250,,,estimate

            """");
}