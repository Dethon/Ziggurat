using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

// The wake metadata on run-pipeline is peer-supplied and optional, and it is read on the Wyoming
// read loop — where an exception drops the satellite connection mid-utterance. Every shape a
// non-conforming or pre-arbitration satellite can send has to come back as a value, never a throw.
public class WakeAnnouncementTests
{
    [Fact]
    public void Read_WakeFrame_ReturnsReportedSignalAndSource()
    {
        var wake = WakeAnnouncement.Read(
            new JsonObject { ["source"] = "wake", ["wake_rms"] = 1234.5, ["wake_score"] = 0.87 });

        wake.Rms.ShouldBe(1234.5);
        wake.Score.ShouldBe(0.87);
        wake.Source.ShouldBe("wake");
    }

    [Fact]
    public void Read_ButtonFrame_ReportsButtonSourceWithNoSignal()
    {
        var wake = WakeAnnouncement.Read(new JsonObject { ["source"] = "button" });

        wake.Rms.ShouldBeNull();
        wake.Score.ShouldBeNull();
        wake.Source.ShouldBe("button");
    }

    // Pre-arbitration firmware sends run-pipeline with no data at all: null signals (which rank
    // last in PickWinner) and the default source, not a torn-down connection.
    [Fact]
    public void Read_EmptyData_ReturnsNoSignalAndDefaultsToWake()
    {
        var wake = WakeAnnouncement.Read([]);

        wake.Rms.ShouldBeNull();
        wake.Score.ShouldBeNull();
        wake.RoomRms.ShouldBeNull();
        wake.Source.ShouldBe("wake");
    }

    // Protocol 1.7: the satellite listens to the room the whole time it is idle, so it can measure
    // the background from audio that contains neither the user's voice nor the capture. The hub
    // cannot — its first frame is already the turn. A satellite that doesn't send it reads as null
    // and the hub falls back to what its own captures have learned.
    [Fact]
    public void Read_WakeFrameWithRoomLevel_ReturnsTheMeasuredRoom()
    {
        var wake = WakeAnnouncement.Read(
            new JsonObject { ["source"] = "wake", ["wake_rms"] = 1234.5, ["room_rms"] = 68.25 });

        wake.RoomRms.ShouldBe(68.25);
    }

    [Fact]
    public void Read_NonNumericSignals_ReturnsNoSignal()
    {
        var wake = WakeAnnouncement.Read(new JsonObject
        {
            ["wake_rms"] = "loud",
            ["wake_score"] = JsonValue.Create((object?)null)
        });

        wake.Rms.ShouldBeNull();
        wake.Score.ShouldBeNull();
    }

    // A non-string source would throw out of JsonValue.GetValue<string>(); an empty one would make
    // the "button" comparison in the arbiter meaningless. Both fall back to the default.
    [Theory]
    [InlineData(7)]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_UnusableSource_FallsBackToWake(object source)
    {
        var wake = WakeAnnouncement.Read(
            new JsonObject { ["source"] = JsonValue.Create(source) });

        wake.Source.ShouldBe("wake");
    }

    [Fact]
    public void Read_ObjectSource_FallsBackToWake()
    {
        var wake = WakeAnnouncement.Read(
            new JsonObject { ["source"] = new JsonObject { ["kind"] = "wake" } });

        wake.Source.ShouldBe("wake");
    }
}