using Infrastructure.Clients.Transcription;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Transcription;

// A sender's word about a container is worth nothing — Telegram documents the mime type as
// optional, and a browser writes whatever it likes on a multipart part — so the leading bytes
// decide. Real files from a real encoder, because a hand-written header proves only that the
// sniffing matches the hand-written header.
public class AudioContainerTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Unit/McpChannelTelegram/Fixtures", name));

    [Fact]
    public void OggOpus_IsTheOneContainerWhisperCannotRead()
    {
        var container = AudioContainer.Sniff(Fixture("voice-note.ogg")).ShouldNotBeNull();

        container.ShouldBe(AudioContainer.OggOpus);
        container.NeedsDecoding.ShouldBeTrue();
    }

    [Theory]
    [InlineData("voice-note.wav", "audio/wav")]
    [InlineData("voice-note.mp3", "audio/mpeg")]
    [InlineData("voice-note.flac", "audio/flac")]
    [InlineData("voice-note-vorbis.ogg", "audio/ogg")]
    public void WhatWhisperDecodesItself_IsPassedThroughUnderItsRealType(string fixture, string mediaType)
    {
        var container = AudioContainer.Sniff(Fixture(fixture));

        container.ShouldNotBeNull();
        container.NeedsDecoding.ShouldBeFalse();
        container.MediaType.ShouldBe(mediaType);
    }

    // An MP3 need not carry an ID3 tag; a bare frame sync is the same file.
    [Fact]
    public void AnMp3WithNoId3Tag_IsStillRecognised()
    {
        AudioContainer.Sniff([0xFF, 0xFB, 0x90, 0x64, 0x00, 0x00])!.MediaType.ShouldBe("audio/mpeg");
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S' })] // a page header and nothing in it
    [InlineData(new byte[0])]
    public void AnythingElse_IsRefusedRatherThanGuessedAt(byte[] bytes)
    {
        AudioContainer.Sniff(bytes).ShouldBeNull();
    }
}