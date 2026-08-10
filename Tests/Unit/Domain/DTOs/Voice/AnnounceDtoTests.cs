using System.Text.Json;
using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Voice;

public class AnnounceDtoTests
{
    [Fact]
    public void AnnounceRequest_RoundTrips_WithSatelliteIdTarget()
    {
        var json = """{"target":{"satelliteId":"kitchen-01"},"text":"hi","priority":"High"}""";
        var req = JsonSerializer.Deserialize<AnnounceRequest>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        req.ShouldNotBeNull();
        req!.Target.SatelliteId.ShouldBe("kitchen-01");
        req.Text.ShouldBe("hi");
        req.Priority.ShouldBe(AnnouncePriority.High);
    }
}