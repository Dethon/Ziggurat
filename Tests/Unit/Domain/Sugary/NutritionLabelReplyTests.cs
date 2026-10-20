using Domain.Tools.Sugary.Estimation;
using Domain.Tools.Sugary.Labels;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// A label's answer is a printed value or a refusal, never a guess: the standalone label reader's
// rules (Sugary.Tests/Packaged/PackagedTests.cs) carried over, read as strictly as an estimate.
public class NutritionLabelReplyTests
{
    private const string Readable =
        """{"readable":true,"carbsPer100g":61.5,"unit":"g","servingGrams":25,"name":"Galletas","note":null}""";

    [Fact]
    public void AReadableLabel_BecomesItsPrintedValues()
    {
        NutritionLabelReply.Read(Readable)
            .ShouldBe(new LabelReading.Read(new PrintedLabel("Galletas", 61.5, 25, "g", null)));
    }

    [Fact]
    public void ALabelPerHundredMillilitres_KeepsItsUnit_AndANoteSheMustKnowGoesWithIt()
    {
        var reading = NutritionLabelReply.Read(
            """{"readable":true,"carbsPer100g":10.6,"unit":"ml","servingGrams":330,"name":" Refresco ","note":" Values are for the drink as sold. "}""");

        reading.ShouldBe(new LabelReading.Read(
            new PrintedLabel("Refresco", 10.6, 330, "ml", "Values are for the drink as sold.")));
    }

    // A serving the label does not state is not a serving of zero grams.
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("-5")]
    public void AServingSizeThatIsNotOne_IsNone(string serving)
    {
        var reading = NutritionLabelReply.Read(Readable.Replace("\"servingGrams\":25", $"\"servingGrams\":{serving}"));

        reading.ShouldBeOfType<LabelReading.Read>().Label.ServingGrams.ShouldBeNull();
    }

    [Fact]
    public void ABlankName_IsNone()
    {
        NutritionLabelReply.Read(Readable.Replace("\"Galletas\"", "\"  \""))
            .ShouldBeOfType<LabelReading.Read>().Label.Name.ShouldBeNull();
    }

    [Theory]
    [InlineData("""{"readable":false,"carbsPer100g":null,"unit":"g","servingGrams":null,"name":null,"note":"Too blurry"}""")]
    [InlineData("""{"readable":true,"carbsPer100g":null,"unit":"g","servingGrams":null,"name":null,"note":"Too blurry"}""")]
    [InlineData("""{"readable":true,"carbsPer100g":640,"unit":"g","servingGrams":null,"name":null,"note":"Too blurry"}""")]
    [InlineData("""{"readable":true,"carbsPer100g":-1,"unit":"g","servingGrams":null,"name":null,"note":"Too blurry"}""")]
    [InlineData("""{"readable":false,"carbsPer100g":40,"unit":"g","servingGrams":null,"name":null,"note":" Too blurry "}""")]
    public void ALabelWithoutAUsableValue_IsRefusedWithTheModelsReason(string reply)
    {
        NutritionLabelReply.Read(reply).ShouldBe(new LabelReading.Unreadable("Too blurry"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"  \"")]
    public void ARefusalWithNoReason_GivesNone_ForTheAppToWordInHerLanguage(string note)
    {
        NutritionLabelReply.Read(
                $$"""{"readable":false,"carbsPer100g":null,"unit":"g","servingGrams":null,"name":null,"note":{{note}}}""")
            .ShouldBe(new LabelReading.Unreadable(null));
    }

    public static TheoryData<string, string> Unreadable => new()
    {
        { "prose", "The label says 61.5 g of carbohydrate per 100 g." },
        { "empty object", "{}" },
        { "a meal estimate", """{"items":[],"questions":[],"notes":[]}""" },
        { "the note missing", Readable.Replace(""","note":null""", "") },
        { "readable missing", Readable.Replace("\"readable\":true,", "") },
        { "a field it never asked for", Readable.Replace("}", ""","insulin":"4 units"}""") },
        { "a number as text", Readable.Replace("61.5", "\"61.5\"") },
        { "readable as text", Readable.Replace("true", "\"yes\"") },
        { "a unit outside the list", Readable.Replace("\"g\"", "\"cl\"") },
        { "a null unit", Readable.Replace("\"g\"", "null") },
        { "two answers", Readable + Readable },
        { "truncated", Readable[..^5] }
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void AReplyOutsideTheSchema_IsRefusedWhole(string what, string reply)
    {
        Should.Throw<UnreadableEstimateException>(() => NutritionLabelReply.Read(reply), what);
    }

    [Fact]
    public void TheSchema_IsOneStrictModeAccepts()
    {
        StrictSchema.Assert(NutritionLabelSchema.Format.Schema);
        NutritionLabelSchema.Format.Name.ShouldBe("nutrition_label");
    }
}