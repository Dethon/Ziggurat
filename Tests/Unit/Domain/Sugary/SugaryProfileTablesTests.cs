using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// Who she is and the pens she uses, as the agent reads them: what she never said is an empty cell, a
// height and a weight each a table of their own oldest first, and a pen's replacement day worked out
// from its own figures.
public class SugaryProfileTablesTests
{
    private static readonly Guid _fiasp = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _tresiba = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void AProfileNeverSaid_IsOneRowOfEmptyCells() =>
        SugaryCsv.Write(SugaryTables.Profile(LoggedProfile.Unsaid)).ShouldBe("""
            sex,diabetes_type,diagnosed_on,born_on
            ,,,

            """);

    [Fact]
    public void AProfile_IsOneRowInTheApisWords() =>
        SugaryCsv.Write(SugaryTables.Profile(new LoggedProfile(Sex.Male, DiabetesType.Lada, new DateOnly(2019, 4, 2), new DateOnly(1980, 1, 31))))
            .ShouldBe("""
                sex,diabetes_type,diagnosed_on,born_on
                male,lada,2019-04-02,1980-01-31

                """);

    [Fact]
    public void HeightsAndWeights_AreTablesOfTheirOwn_OldestFirst()
    {
        LoggedMeasurement[] measurements =
        [
            new(Guid.NewGuid(), BodyMeasure.Height, new DateOnly(2026, 9, 1), 154),
            new(Guid.NewGuid(), BodyMeasure.Weight, new DateOnly(2026, 9, 1), 41.2),
            new(Guid.NewGuid(), BodyMeasure.Height, new DateOnly(2026, 3, 1), 151.5)
        ];

        SugaryCsv.Write(SugaryTables.Heights(measurements)).ShouldBe("""
            date,height_cm
            2026-03-01,151.5
            2026-09-01,154

            """);
        SugaryCsv.Write(SugaryTables.Weights(measurements)).ShouldBe("""
            date,weight_kg
            2026-09-01,41.2

            """);
    }

    [Fact]
    public void Pens_AreOneRowEach_InTheOrderOpened_WithTheirReplacementDay()
    {
        LoggedPen[] pens =
        [
            new(_fiasp, DoseKind.Rapid, "Fiasp", new DateOnly(2026, 9, 1), 28, new DateOnly(2026, 9, 20)),
            new(_tresiba, DoseKind.Basal, "Tresiba", new DateOnly(2026, 8, 15), 56, null)
        ];

        SugaryCsv.Write(SugaryTables.Pens(pens)).ShouldBe("""
            pen_id,kind,insulin,opened_on,days,replace_on,replaced_on
            22222222-2222-2222-2222-222222222222,basal,Tresiba,2026-08-15,56,2026-10-10,
            11111111-1111-1111-1111-111111111111,rapid,Fiasp,2026-09-01,28,2026-09-29,2026-09-20

            """);
    }

    // Weight, diabetes type and an insulin's name invite a dose worked out from them; the README says
    // they are context, never a ratio or an action time.
    [Fact]
    public void TheReadme_ExplainsEveryNewColumn_AndThatNoneIsANumberToDoseFrom()
    {
        var readme = SugaryReadme.For(TimeZoneInfo.Utc, null);

        string[] columns =
        [
            "sex", "diabetes_type", "diagnosed_on", "born_on", "height_cm", "weight_kg", "pen_id", "insulin", "opened_on",
            "replace_on", "replaced_on", "type1", "lada", "gestational"
        ];
        columns.Where(column => !readme.Contains($"`{column}`")).ShouldBeEmpty();
        readme.ShouldContain("## profile.parquet");
        readme.ShouldContain("## heights.parquet and weights.parquet");
        readme.ShouldContain("## pens.parquet");
        readme.ShouldContain("never a number to work");
        readme.ShouldContain("never take an action time or a dose from it");
    }
}