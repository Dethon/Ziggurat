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
        var result = HaActionResolver.ServicesFor("light.kitchen", _services)
            .Select(s => s.Service).ToList();
        result.ShouldBe(["toggle", "turn_on"]);
    }

    [Fact]
    public void ServicesFor_ReadOnlyEntity_ReturnsEmpty()
    {
        // sensor has no class-domain entity-targeted services here.
        HaActionResolver.ServicesFor("sensor.salon_temp", _services).ShouldBeEmpty();
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

        var names = HaActionResolver.ServicesFor("media_player.office", services)
            .Select(s => HaActionResolver.CommandName(s, "media_player")).ToList();

        names.ShouldContain("music_assistant.play_media");
        names.ShouldContain("play_media");
        names.ShouldNotContain("turn_on");
    }
}