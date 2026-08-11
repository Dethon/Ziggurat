using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaArgParserTests
{
    private static HaServiceField Field(JsonNode? selector) =>
        new() { Selector = selector };

    private static HaServiceDefinition Svc() => Service("light", "turn_on", AnyEntityTarget(),
        ("brightness_pct", Field(JsonNode.Parse("""{"number":{"min":1,"max":100}}"""))),
        ("on", Field(JsonNode.Parse("""{"boolean":{}}"""))),
        ("modes", Field(JsonNode.Parse("""{"select":{"multiple":true,"options":["a","b"]}}"""))),
        ("flash", Field(JsonNode.Parse("""{"select":{"options":[{"value":"short"},{"value":"long"}]}}"""))),
        ("advanced", Field(JsonNode.Parse("""{"object":{}}"""))),
        ("name", Field(JsonNode.Parse("""{"text":{}}"""))));

    [Fact]
    public void Parse_CoercesBySelectorType()
    {
        var data = HaArgParser.Parse(
            ["--brightness_pct", "60", "--on", "true", "--modes", "a,b", "--advanced", """{"eco":true}""", "--name", "Lamp"],
            Svc());

        data["brightness_pct"]!.GetValue<int>().ShouldBe(60);
        data["on"]!.GetValue<bool>().ShouldBeTrue();
        ((JsonArray)data["modes"]!).Count.ShouldBe(2);
        data["advanced"]!["eco"]!.GetValue<bool>().ShouldBeTrue();
        data["name"]!.GetValue<string>().ShouldBe("Lamp");
    }

    [Theory]
    [InlineData(new[] { "--nope", "1" }, "nope")]                          // unknown flag
    [InlineData(new[] { "--on", "yes" }, "on")]                            // bad boolean
    [InlineData(new[] { "--brightness_pct", "NaN" }, "brightness_pct")]   // bad number
    [InlineData(new[] { "--flash", "bogus" }, "flash")]                    // invalid select option
    [InlineData(new[] { "--name", "--on" }, "name")]                       // space-form value looks like flag
    public void Parse_InvalidInput_Throws(string[] args, string messageContains)
    {
        Should.Throw<ArgumentException>(() => HaArgParser.Parse(args, Svc()))
            .Message.ShouldContain(messageContains);
    }

    [Fact]
    public void Parse_SingleSelectValidOption_Passes()
    {
        HaArgParser.Parse(["--flash", "short"], Svc())["flash"]!.GetValue<string>().ShouldBe("short");
    }

    [Fact]
    public void Parse_EqualsSyntax_CoercesBySelectorType()
    {
        var data = HaArgParser.Parse(
            ["--brightness_pct=60", "--name=Lamp", """--advanced={"eco":true}"""],
            Svc());

        data["brightness_pct"]!.GetValue<int>().ShouldBe(60);
        data["name"]!.GetValue<string>().ShouldBe("Lamp");
        data["advanced"]!["eco"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public void Parse_EqualsSyntax_SplitsOnFirstEqualsOnly()
    {
        HaArgParser.Parse(["--name=a=b"], Svc())["name"]!.GetValue<string>().ShouldBe("a=b");
    }

    [Fact]
    public void Parse_SpaceFormSingleDashValue_IsAccepted()
    {
        // Only '--' starts a flag; a single '-' (e.g. a negative number) is a valid value.
        HaArgParser.Parse(["--brightness_pct", "-5"], Svc())["brightness_pct"]!.GetValue<int>().ShouldBe(-5);
    }

    [Fact]
    public void Parse_ObjectSelector_BareText_FallsBackToString()
    {
        HaArgParser.Parse(["--advanced", "chill relaxing music"], Svc())["advanced"]!
            .GetValue<string>().ShouldBe("chill relaxing music");
    }

    [Fact]
    public void Parse_ObjectSelector_QuotedJsonString_StillParses()
    {
        HaArgParser.Parse(["--advanced", "\"Liked Songs\""], Svc())["advanced"]!
            .GetValue<string>().ShouldBe("Liked Songs");
    }

    [Fact]
    public void Parse_ObjectSelector_JsonArray_StillParses()
    {
        ((JsonArray)HaArgParser.Parse(["--advanced", """["a","b"]"""], Svc())["advanced"]!)
            .Count.ShouldBe(2);
    }

    // Media titles that happen to be valid JSON scalars ("1979", "22", "true", "null") must stay
    // strings: Music Assistant resolves media_id by name, and a JSON number/bool/null reaches it as
    // a non-string it cannot look up, which HA surfaces as a 500.
    [Theory]
    [InlineData("1979")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("1.20")]
    public void Parse_ObjectSelector_JsonScalarLookalikeTitle_StaysString(string title)
    {
        HaArgParser.Parse(["--advanced", title], Svc())["advanced"]!
            .GetValue<string>().ShouldBe(title);
    }
}