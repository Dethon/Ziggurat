using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteRegistryTests
{
    private static readonly Dictionary<string, SatelliteConfig> _sample = new()
    {
        ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" },
        ["living-room-01"] = new() { Identity = "household", Room = "Living Room", WakeWord = "hey_jarvis" },
        ["bedroom-01"] = new() { Identity = "francisco", Room = "Bedroom", WakeWord = "hey_jarvis" }
    };

    [Fact]
    public void GetById_KnownSatellite_ReturnsConfig()
    {
        var registry = new SatelliteRegistry(_sample);
        var sat = registry.GetById("kitchen-01");
        sat.ShouldNotBeNull();
        sat!.Identity.ShouldBe("household");
        sat.Room.ShouldBe("Kitchen");
    }

    [Fact]
    public void GetIdsByRoom_MatchesCaseInsensitive()
    {
        var registry = new SatelliteRegistry(_sample);
        var ids = registry.GetIdsByRoom("kitchen");
        ids.ShouldBe(["kitchen-01"]);
    }

    [Fact]
    public void GetAllIds_ReturnsEverySatellite()
    {
        var registry = new SatelliteRegistry(_sample);
        registry.GetAllIds().ShouldBe(["kitchen-01", "living-room-01", "bedroom-01"], ignoreOrder: true);
    }

    [Fact]
    public void GetIdsByRoom_DisplayLocationForm_ResolvesSatellite()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", Locality = "Madrid, Spain" }
        });

        // The agent is never shown the bare Room — both the satellite catalog prompt and the
        // per-message header carry DisplayLocation — so a room copied from either must route, or
        // the target silently matches nothing and the announcement never plays.
        registry.GetIdsByRoom("Kitchen (Madrid, Spain)").ShouldBe(["kitchen-01"]);
        registry.GetIdsByRoom("Kitchen").ShouldBe(["kitchen-01"]);
    }

    [Fact]
    public void Resolve_AppliesTargetPrecedence()
    {
        var registry = new SatelliteRegistry(_sample);

        registry.Resolve(new AnnounceTarget { SatelliteIds = ["kitchen-01", "ghost-01"] }).ShouldBe(["kitchen-01"]);
        registry.Resolve(new AnnounceTarget { SatelliteId = "ghost-01" }).ShouldBeEmpty();
        registry.Resolve(new AnnounceTarget { Room = "Bedroom" }).ShouldBe(["bedroom-01"]);
        registry.Resolve(new AnnounceTarget { Room = "Basement" }).ShouldBeEmpty();
        registry.Resolve(new AnnounceTarget { All = true }).Count.ShouldBe(3);
        registry.Resolve(new AnnounceTarget()).ShouldBeEmpty();

        // Precedence, not just presence: with several dimensions set at once the earlier one wins,
        // so a reordering of Resolve's branches (e.g. All checked first) would fan out wrongly and
        // must fail here rather than pass because every case set a single field.
        registry.Resolve(new AnnounceTarget { SatelliteIds = ["kitchen-01"], All = true }).ShouldBe(["kitchen-01"]);
        registry.Resolve(new AnnounceTarget { SatelliteId = "bedroom-01", Room = "Kitchen", All = true }).ShouldBe(["bedroom-01"]);
        registry.Resolve(new AnnounceTarget { Room = "Bedroom", All = true }).ShouldBe(["bedroom-01"]);
    }

    [Fact]
    public void Resolve_NullEntryInSatelliteIds_IsIgnored()
    {
        var registry = new SatelliteRegistry(_sample);

        // SatelliteIds arrives from LLM-authored JSON, where a null element is expressible.
        // Dictionary.TryGetValue(null) throws, so it must be filtered before the lookup.
        registry.Resolve(new AnnounceTarget { SatelliteIds = [null!, "kitchen-01"] }).ShouldBe(["kitchen-01"]);
    }
}