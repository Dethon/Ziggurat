using System.Text.Json.Nodes;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionControlTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static WyomingEvent Event() =>
        WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = "up" });

    // No writer means the satellite is not connected. A fast-path command must not throw on the
    // dispatch path just because a connection went away between transcript and action.
    [Fact]
    public async Task TrySendControlAsync_NoWriter_ReturnsFalse()
    {
        (await Session().TrySendControlAsync(Event(), default)).ShouldBeFalse();
    }

    [Fact]
    public async Task TrySendControlAsync_WriterThrows_ReturnsFalseWithoutPropagating()
    {
        var session = Session();
        session.ControlWriter = (_, _) => throw new IOException("socket closed");

        (await session.TrySendControlAsync(Event(), default)).ShouldBeFalse();
    }
}