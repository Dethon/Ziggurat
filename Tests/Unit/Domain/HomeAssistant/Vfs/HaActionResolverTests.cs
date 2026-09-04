using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaActionResolverTests
{
    private static readonly List<HaServiceDefinition> _services =
    [
        Service("light", "turn_on", AnyEntityTarget()),
        Service("light", "toggle", DomainTarget("light")),
        Service("light", "no_target", null),                 // not entity-targeted
        Service("vacuum", "start", DomainTarget("vacuum")),  // wrong class domain
        Service("homeassistant", "restart", null)
    ];

    [Fact]
    public void ServicesFor_ReturnsClassDomainTargetedServices_Sorted()
    {
        var result = HaActionResolver.ServicesFor(Entity("light.kitchen", "off"), _services)
            .Select(s => s.Service).ToList();
        result.ShouldBe(["toggle", "turn_on"]);
    }

    [Fact]
    public void ServicesFor_ReadOnlyEntity_ReturnsEmpty()
    {
        // sensor has no class-domain entity-targeted services here.
        HaActionResolver.ServicesFor(Entity("sensor.salon_temp", "off"), _services).ShouldBeEmpty();
    }

    [Fact]
    public void ServicesFor_IncludesCrossDomainService_ThatExplicitlyTargetsThisClass()
    {
        // Music Assistant augments media_player entities with `music_assistant.play_media`
        // (a different domain that explicitly targets `media_player`). It must be exposed
        // alongside the same-domain `media_player.play_media`, while a generic global service
        // (`homeassistant.turn_on`, target = any entity) stays excluded so directories aren't flooded.
        var services = new List<HaServiceDefinition>
        {
            Service("media_player", "play_media", DomainTarget("media_player")),
            Service("music_assistant", "play_media", DomainTarget("media_player")),
            Service("homeassistant", "turn_on", AnyEntityTarget())
        };

        var names = HaActionResolver.ServicesFor(Entity("media_player.office", "off"), services)
            .Select(s => HaActionResolver.CommandName(s, "media_player")).ToList();

        names.ShouldContain("music_assistant.play_media");
        names.ShouldContain("play_media");
        names.ShouldNotContain("turn_on");
    }
}
public class HaActionResolverEveryEntityTests
{
    private static readonly HaServiceDefinition _history = new()
    {
        Domain = "homeassistant",
        Service = "history",
        AppliesToEveryEntity = true
    };

    // The generic `homeassistant.*` services are kept out of every directory because their target
    // accepts anything; an action that declares itself for every entity is the one exception, and
    // it reaches the read-only classes too.
    [Fact]
    public void ServicesFor_AnEveryEntityAction_AppearsInEveryClass_UnderItsBareName()
    {
        List<HaServiceDefinition> services =
        [
            Service("light", "turn_on", DomainTarget("light")),
            Service("homeassistant", "restart", null),
            _history
        ];

        HaActionResolver.ServicesFor(Entity("sensor.glucose", "off"), services).ShouldBe([_history]);
        HaActionResolver.ServicesFor(Entity("light.kitchen", "off"), services)
            .Select(s => HaActionResolver.CommandName(s, "light"))
            .ShouldBe(["history", "turn_on"]);
    }
}
public class HaActionResolverRequiredAttributeTests
{
    private static readonly HaServiceDefinition _statistics = new()
    {
        Domain = "homeassistant",
        Service = "statistics",
        AppliesToEveryEntity = true,
        RequiresAttribute = "state_class"
    };

    // Long-term statistics exist only for a sensor whose state carries a state_class, so the action
    // that reads them is offered to exactly those — an every-entity action narrowed by attribute.
    [Fact]
    public void ServicesFor_ARequiredAttribute_AdmitsOnlyTheEntitiesCarryingIt()
    {
        List<HaServiceDefinition> services = [_statistics];
        var measured = Entity("sensor.temperature", "21", ("state_class", JsonValue.Create("measurement")));
        var bare = Entity("sensor.glucose", "94");
        var light = Entity("light.kitchen", "off", ("state_class", JsonValue.Create("measurement")));

        HaActionResolver.ServicesFor(measured, services).ShouldBe([_statistics]);
        HaActionResolver.ServicesFor(bare, services).ShouldBeEmpty();
        HaActionResolver.ServicesFor(light, services).ShouldBe([_statistics]);
        HaActionResolver.CommandName(_statistics, "sensor").ShouldBe("statistics");
    }

    // Carrying the attribute means having a value for it: a `state_class: null` is no state_class.
    [Fact]
    public void ServicesFor_ARequiredAttributeWithANullValue_DoesNotCount()
    {
        var nulled = Entity("sensor.glucose", "94", ("state_class", null));

        HaActionResolver.ServicesFor(nulled, [_statistics]).ShouldBeEmpty();
    }
}