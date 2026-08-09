using System.Text.Json.Nodes;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaCatalogTests
{
    private static HaCatalog Sample() => new(
        [Entity("light.kitchen", "off"), Entity("light.salon", "on"), Entity("sensor.salon_temp", "21")],
        [],
        [new HaAreaEntities("salon", "Salón", ["light.salon", "sensor.salon_temp"])]);

    [Fact]
    public void ClassDomains_ReturnsSortedDistinctPrefixes()
    {
        Sample().ClassDomains().ShouldBe(["light", "sensor"]);
    }

    [Fact]
    public void ObjectIdsFor_ReturnsObjectIdsOfThatClass()
    {
        Sample().ObjectIdsFor("light").ShouldBe(["kitchen", "salon"]);
    }

    [Fact]
    public void AreaSlugs_IncludesUnassignedWhenSomeEntityHasNoArea()
    {
        // light.kitchen is in no area -> "unassigned" bucket appears.
        Sample().AreaSlugs().ShouldBe(["salon", "unassigned"]);
    }

    [Fact]
    public void EntityIdsInArea_ReturnsAssigned_AndUnassignedBucket()
    {
        Sample().EntityIdsInArea("salon").ShouldBe(["light.salon", "sensor.salon_temp"]);
        Sample().EntityIdsInArea("unassigned").ShouldBe(["light.kitchen"]);
    }

    [Fact]
    public void FriendlyName_ReadsAttribute_OrNull()
    {
        var withName = Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen")));
        var without = Entity("light.hall", "off");
        HaCatalog.FriendlyName(withName).ShouldBe("Kitchen");
        HaCatalog.FriendlyName(without).ShouldBeNull();
        HaCatalog.FriendlyName(null).ShouldBeNull();
    }
}