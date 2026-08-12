using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SpeakerVolumeCommandHandlerTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Theory]
    [InlineData(VoiceCommand.LocalVolumeUp, "up")]
    [InlineData(VoiceCommand.LocalVolumeDown, "down")]
    [InlineData(VoiceCommand.LocalMute, "mute")]
    [InlineData(VoiceCommand.LocalUnmute, "unmute")]
    public async Task HandleAsync_EachCommand_SendsSpeakerVolumeWithItsAction(VoiceCommand command, string action)
    {
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };
        var handler = new SpeakerVolumeCommandHandler();

        var sent = await handler.HandleAsync(command, session, default);

        sent.ShouldBeTrue();
        written.Count.ShouldBe(1);
        written[0].Type.ShouldBe("speaker-volume");
        written[0].Data["action"]!.GetValue<string>().ShouldBe(action);
    }
}