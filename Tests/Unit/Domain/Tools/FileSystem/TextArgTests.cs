using System.Text.Json;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class TextArgTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Theory]
    [InlineData("\"hello\"", "hello", false)]
    [InlineData("{\"a\":1}", "{\"a\":1}", true)]
    [InlineData("null", "", true)]
    public void Coerce_And_WasCoerced_ByValueKind(string rawJson, string expectedText, bool expectedCoerced)
    {
        var raw = Json(rawJson);
        TextArg.Coerce(raw).ShouldBe(expectedText);
        TextArg.WasCoerced(raw).ShouldBe(expectedCoerced);
    }

    [Fact]
    public void Coerce_BareClrString_PassesThrough_NotCoerced()
    {
        TextArg.Coerce("x").ShouldBe("x");
        TextArg.WasCoerced("x").ShouldBeFalse();
    }

    [Fact]
    public void Coerce_Null_IsEmpty_NotCoerced()
    {
        TextArg.Coerce(null).ShouldBe(string.Empty);
        TextArg.WasCoerced(null).ShouldBeFalse();
    }

    [Fact]
    public void WasCoercedArg_TrueWhenPresentArgIsObject_FalseWhenAbsentOrString()
    {
        var args = new AIFunctionArguments { ["content"] = Json("{\"a\":1}") };
        TextArg.WasCoercedArg(args, "content").ShouldBeTrue();

        var stringArgs = new AIFunctionArguments { ["content"] = Json("\"hi\"") };
        TextArg.WasCoercedArg(stringArgs, "content").ShouldBeFalse();

        TextArg.WasCoercedArg(new AIFunctionArguments(), "content").ShouldBeFalse();
        TextArg.WasCoercedArg(null, "content").ShouldBeFalse();
    }

    [Fact]
    public void CoerceEdits_CoercesStructuredFields_KeepsStrings_AndReplaceAll()
    {
        var raw = Json("[{\"oldString\":\"a\",\"newString\":{\"k\":1}},{\"oldString\":\"b\",\"newString\":\"c\",\"replaceAll\":true}]");
        var edits = TextArg.CoerceEdits(raw);

        edits.Count.ShouldBe(2);
        edits[0].OldString.ShouldBe("a");
        edits[0].NewString.ShouldBe("{\"k\":1}");
        edits[0].ReplaceAll.ShouldBeFalse();
        edits[1].OldString.ShouldBe("b");
        edits[1].NewString.ShouldBe("c");
        edits[1].ReplaceAll.ShouldBeTrue();
    }

    [Fact]
    public void CoerceEdits_NonArray_ReturnsEmpty()
    {
        TextArg.CoerceEdits(Json("{\"not\":\"an array\"}")).ShouldBeEmpty();
        TextArg.CoerceEdits(null).ShouldBeEmpty();
    }

    [Fact]
    public void EditsWereCoercedArg_TrueWhenAnyFieldStructured()
    {
        var coerced = new AIFunctionArguments { ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":{\"k\":1}}]") };
        TextArg.EditsWereCoercedArg(coerced).ShouldBeTrue();

        var clean = new AIFunctionArguments { ["edits"] = Json("[{\"oldString\":\"a\",\"newString\":\"b\"}]") };
        TextArg.EditsWereCoercedArg(clean).ShouldBeFalse();

        TextArg.EditsWereCoercedArg(new AIFunctionArguments()).ShouldBeFalse();
        TextArg.EditsWereCoercedArg(null).ShouldBeFalse();
    }
}