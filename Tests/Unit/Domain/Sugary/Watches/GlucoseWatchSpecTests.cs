using System.Text.Json.Nodes;
using Domain.Tools.Sugary.Watches;
using Shouldly;

namespace Tests.Unit.Domain.Sugary.Watches;

// What the agent writes to /watchers/<id>/watch.json for a glucose watch. Every refusal names the
// field, because the agent fixes the file from the message in the same turn.
public class GlucoseWatchSpecTests
{
    private const string Prompt = """[{"kind":"prompt","prompt":"Tell her she is low"}]""";

    private static string Watch(string triggers, string? effects = null, string extra = "") =>
        $$"""{"name":"Low","triggers":{{triggers}},"effects":{{effects ?? Prompt}}{{extra}}}""";

    [Fact]
    public void Parse_EveryTriggerKind_ReadsItsFields()
    {
        var spec = GlucoseWatchSpec.Parse(Watch("""
            [
              {"trigger":"glucose","below":70,"for":{"minutes":10}},
              {"trigger":"glucose_trend","falling":2.5},
              {"trigger":"glucose_forecast","above":250,"within":{"minutes":20}},
              {"trigger":"sensor","state":"no_readings","for":{"minutes":30}},
              {"trigger":"sensor","state":"link_failing","for":{"hours":1}},
              {"trigger":"sensor","state":"ending","within":{"hours":24}}
            ]
            """));

        spec.Triggers.ShouldBe([
            new GlucoseLevelTrigger(GlucoseBound.Below, 70, TimeSpan.FromMinutes(10)),
            new GlucoseTrendTrigger(GlucoseTrendDirection.Falling, 2.5),
            new GlucoseForecastTrigger(GlucoseBound.Above, 250, TimeSpan.FromMinutes(20)),
            new SensorTrigger(SensorState.NoReadings, For: TimeSpan.FromMinutes(30)),
            new SensorTrigger(SensorState.LinkFailing, For: TimeSpan.FromHours(1)),
            new SensorTrigger(SensorState.Ending, Within: TimeSpan.FromHours(24))
        ]);
        spec.Name.ShouldBe("Low");
        spec.Enabled.ShouldBeTrue();
        spec.Once.ShouldBeFalse();
    }

    [Fact]
    public void Parse_APerson_IsKeptOnItsTrigger()
    {
        var spec = GlucoseWatchSpec.Parse(Watch("""[{"trigger":"glucose","below":70,"person":"tradaly"}]"""));

        spec.Triggers.Single().Person.ShouldBe("tradaly");
    }

    [Fact]
    public void Parse_TimeConditionsAndBothEffects_AreRead()
    {
        var spec = GlucoseWatchSpec.Parse(Watch(
            """[{"trigger":"glucose","above":250}]""",
            """[{"kind":"prompt","prompt":"Ask her"},{"kind":"announce","text":"Glucose is high","target":{"room":"kitchen"},"insistent":{}}]""",
            ""","conditions":[{"condition":"time","after":"22:00","before":"07:00:00","weekday":["mon","tue"]}],"once":true,"enabled":false,"deliverTo":["telegram"],"userId":"user-dethon" """));

        spec.Conditions.ShouldNotBeNull().Count.ShouldBe(1);
        spec.Effects.Count.ShouldBe(2);
        spec.Effects[1].ShouldBeOfType<GlucoseAnnounceEffect>().Target.ShouldNotBeNull()["room"]!.GetValue<string>().ShouldBe("kitchen");
        spec.Once.ShouldBeTrue();
        spec.Enabled.ShouldBeFalse();
        spec.DeliverTo.ShouldBe(["telegram"]);
        spec.UserId.ShouldBe("user-dethon");
    }

    // A glucose alarm with no satellite named rings on its people's channels.
    [Fact]
    public void Parse_AnAnnouncementWithNoTarget_IsAccepted()
    {
        var spec = GlucoseWatchSpec.Parse(Watch("""[{"trigger":"glucose","below":70}]""",
            """[{"kind":"announce","text":"Glucose is low"}]"""));

        spec.Effects.Single().ShouldBeOfType<GlucoseAnnounceEffect>().Target.ShouldBeNull();
    }

    [Fact]
    public void ToJson_ReadsBackAsTheSameWatch()
    {
        var spec = GlucoseWatchSpec.Parse(Watch(
            """[{"trigger":"glucose","below":70,"for":{"minutes":10}},{"trigger":"sensor","state":"ending","within":{"hours":24}}]""",
            """[{"kind":"announce","text":"Low","target":{"all":true}}]""",
            ""","conditions":[{"condition":"time","after":"22:00"}]"""));

        var again = GlucoseWatchSpec.Parse(spec.ToJson());

        again.ToJson().ShouldBe(spec.ToJson());
        again.Triggers.ShouldBe(spec.Triggers);
        JsonNode.Parse(spec.ToJson())!["triggers"]![0]!["for"]!.ToJsonString().ShouldBe("""{"minutes":10}""");
    }

    [Theory]
    // The file
    [InlineData("not json", "not valid JSON")]
    [InlineData("""[]""", "must be a JSON object")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"prompt","prompt":"x"}],"colour":"red"}""", "unknown field 'colour'")]
    [InlineData("""{"triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"prompt","prompt":"x"}]}""", "name is required")]
    [InlineData("""{"name":"Low","triggers":[],"effects":[{"kind":"prompt","prompt":"x"}]}""", "triggers must be a non-empty list")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[]}""", "effects must be a non-empty list")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"prompt","prompt":"x"}],"once":"yes"}""", "once must be true or false")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"prompt","prompt":"x"}],"enabled":1}""", "enabled must be true or false")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"prompt","prompt":"x"}],"deliverTo":"telegram"}""", "deliverTo must be a list")]
    [InlineData("""{"name":"Low","triggers":[{"trigger":"glucose","below":70}],"effects":[{"kind":"prompt","prompt":"x"}],"userId":7}""", "userId must be a string")]
    public void Parse_ARefusedFile_NamesTheField(string content, string expected)
    {
        Should.Throw<GlucoseWatchSpecException>(() => GlucoseWatchSpec.Parse(content)).Message.ShouldContain(expected);
    }

    [Theory]
    [InlineData("""{"below":70}""", "triggers[0].trigger is required")]
    [InlineData("""{"trigger":"state","entity_id":"x"}""", "triggers[0].trigger 'state' is not")]
    [InlineData("""{"trigger":"glucose"}""", "triggers[0] needs one of below or above")]
    [InlineData("""{"trigger":"glucose","below":70,"above":180}""", "triggers[0] needs one of below or above, not both")]
    [InlineData("""{"trigger":"glucose","below":"low"}""", "triggers[0].below must be a number")]
    [InlineData("""{"trigger":"glucose","below":3.9}""", "triggers[0].below")]
    [InlineData("""{"trigger":"glucose","below":70,"for":15}""", "triggers[0].for must be a duration")]
    [InlineData("""{"trigger":"glucose","below":70,"for":{"weeks":1}}""", "triggers[0].for")]
    [InlineData("""{"trigger":"glucose","below":70,"for":{"minutes":0}}""", "triggers[0].for must be longer than zero")]
    [InlineData("""{"trigger":"glucose","below":70,"within":{"minutes":5}}""", "triggers[0].within is not a field of a glucose trigger")]
    [InlineData("""{"trigger":"glucose","below":70,"person":""}""", "triggers[0].person")]
    [InlineData("""{"trigger":"glucose_trend"}""", "triggers[0] needs one of falling or rising")]
    [InlineData("""{"trigger":"glucose_trend","falling":-2}""", "triggers[0].falling must be a positive number")]
    [InlineData("""{"trigger":"glucose_trend","falling":2,"for":{"minutes":5}}""", "triggers[0].for is not a field of a glucose_trend trigger")]
    [InlineData("""{"trigger":"glucose_forecast","below":70}""", "triggers[0].within is required")]
    [InlineData("""{"trigger":"glucose_forecast","within":{"minutes":20}}""", "triggers[0] needs one of below or above")]
    [InlineData("""{"trigger":"sensor"}""", "triggers[0].state is required")]
    [InlineData("""{"trigger":"sensor","state":"dead"}""", "triggers[0].state 'dead' is not one of")]
    [InlineData("""{"trigger":"sensor","state":"no_readings"}""", "triggers[0].for is required")]
    [InlineData("""{"trigger":"sensor","state":"link_failing","within":{"hours":1}}""", "triggers[0].within is not a field of a sensor link_failing trigger")]
    [InlineData("""{"trigger":"sensor","state":"ending"}""", "triggers[0].within is required")]
    [InlineData("""{"trigger":"sensor","state":"ending","within":{"hours":24},"for":{"hours":1}}""", "triggers[0].for is not a field of a sensor ending trigger")]
    public void Parse_ARefusedTrigger_NamesTheField(string trigger, string expected)
    {
        Should.Throw<GlucoseWatchSpecException>(() => GlucoseWatchSpec.Parse(Watch($"[{trigger}]")))
            .Message.ShouldContain(expected);
    }

    // The second trigger is the one named, so the agent fixes the right one.
    [Fact]
    public void Parse_ARefusedSecondTrigger_NamesItsIndex()
    {
        Should.Throw<GlucoseWatchSpecException>(() => GlucoseWatchSpec.Parse(Watch(
                """[{"trigger":"glucose","below":70},{"trigger":"glucose","below":"x"}]""")))
            .Message.ShouldContain("triggers[1].below must be a number");
    }

    [Theory]
    [InlineData("""{"condition":"state"}""", "conditions[0].condition 'state' is not")]
    [InlineData("""{"after":"22:00"}""", "conditions[0].condition is required")]
    [InlineData("""{"condition":"time"}""", "conditions[0] needs after, before or weekday")]
    [InlineData("""{"condition":"time","after":"late"}""", "conditions[0].after must be a time")]
    [InlineData("""{"condition":"time","before":"25:00"}""", "conditions[0].before must be a time")]
    [InlineData("""{"condition":"time","weekday":"monday"}""", "conditions[0].weekday")]
    [InlineData("""{"condition":"time","weekday":["mon","funday"]}""", "conditions[0].weekday")]
    [InlineData("""{"condition":"time","after":"22:00","entity_id":"x"}""", "conditions[0].entity_id is not a field")]
    public void Parse_ARefusedCondition_NamesTheField(string condition, string expected)
    {
        Should.Throw<GlucoseWatchSpecException>(() => GlucoseWatchSpec.Parse(
                Watch("""[{"trigger":"glucose","below":70}]""", extra: $",\"conditions\":[{condition}]")))
            .Message.ShouldContain(expected);
    }

    [Theory]
    [InlineData("""{"prompt":"x"}""", "effects[0].kind is required")]
    [InlineData("""{"kind":"shout"}""", "effects[0].kind 'shout' is not one of prompt, announce")]
    [InlineData("""{"kind":"prompt"}""", "effects[0].prompt is required")]
    [InlineData("""{"kind":"prompt","prompt":"  "}""", "effects[0].prompt must not be empty")]
    [InlineData("""{"kind":"announce"}""", "effects[0].text is required")]
    [InlineData("""{"kind":"announce","text":"Low","target":"kitchen"}""", "effects[0].target must be an object")]
    [InlineData("""{"kind":"announce","text":"Low","insistent":true}""", "effects[0].insistent must be an object")]
    [InlineData("""{"kind":"prompt","prompt":"x","volume":3}""", "effects[0].volume is not a field")]
    public void Parse_ARefusedEffect_NamesTheField(string effect, string expected)
    {
        Should.Throw<GlucoseWatchSpecException>(() => GlucoseWatchSpec.Parse(
                Watch("""[{"trigger":"glucose","below":70}]""", $"[{effect}]")))
            .Message.ShouldContain(expected);
    }

    // A watch that switches something on in the home is a home watch: Sugary acts on nothing there.
    [Fact]
    public void Parse_AnActionsEffect_IsRefusedPointingAtHomeWatches()
    {
        var refused = Should.Throw<GlucoseWatchSpecException>(() => GlucoseWatchSpec.Parse(Watch(
            """[{"trigger":"glucose","below":70}]""",
            """[{"kind":"actions","actions":[{"action":"light.turn_on"}]}]""")));

        refused.Message.ShouldContain("effects[0].kind 'actions'");
        refused.Message.ShouldContain("home watch");
    }
}