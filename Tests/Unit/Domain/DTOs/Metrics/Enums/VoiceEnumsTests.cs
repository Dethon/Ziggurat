using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

public class VoiceEnumsTests
{
    // VoiceMetric/VoiceDimension are persisted as integers in Redis metric events, so their numeric
    // values are part of the wire format. Renumbering re-labels historical data (removing AudioSeconds
    // once shifted every later value). These guards pin the contract: only ever append new members.
    [Theory]
    [InlineData(VoiceMetric.WakeTriggered, 0)]
    [InlineData(VoiceMetric.UtteranceTranscribed, 1)]
    [InlineData(VoiceMetric.SttLatencyMs, 2)]
    [InlineData(VoiceMetric.TtsLatencyMs, 3)]
    [InlineData(VoiceMetric.WakeToFirstAudioMs, 4)]
    [InlineData(VoiceMetric.ApprovalResolved, 5)]
    [InlineData(VoiceMetric.SttError, 6)]
    [InlineData(VoiceMetric.TtsError, 7)]
    [InlineData(VoiceMetric.AnnouncePlayed, 8)]
    [InlineData(VoiceMetric.AnnounceQueued, 9)]
    [InlineData(VoiceMetric.AnnounceError, 10)]
    [InlineData(VoiceMetric.AnnouncePreemptedReply, 11)]
    [InlineData(VoiceMetric.FollowUpWindowOpened, 12)]
    [InlineData(VoiceMetric.FollowUpEngaged, 13)]
    [InlineData(VoiceMetric.FollowUpTimedOut, 14)]
    [InlineData(VoiceMetric.AlarmAcknowledged, 15)]
    [InlineData(VoiceMetric.AlarmUnacknowledged, 16)]
    [InlineData(VoiceMetric.AlarmOffline, 17)]
    [InlineData(VoiceMetric.UtteranceRejected, 18)]
    public void VoiceMetric_HasPinnedWireValues(VoiceMetric metric, int expected) =>
        ((int)metric).ShouldBe(expected);

    [Theory]
    [InlineData(VoiceMetric.TseInvoked, 19)]
    [InlineData(VoiceMetric.TseSkipped, 20)]
    [InlineData(VoiceMetric.TseFailed, 21)]
    [InlineData(VoiceMetric.TseLatencyMs, 22)]
    public void VoiceMetric_TseValues_ArePinned(VoiceMetric metric, int expected)
    {
        // Values persist as ints in Redis; a renumber silently re-labels historical data.
        ((int)metric).ShouldBe(expected);
    }

    [Theory]
    [InlineData(VoiceMetric.EndpointTailMs, 23)]
    [InlineData(VoiceMetric.SpeakerVerifyMs, 24)]
    [InlineData(VoiceMetric.AgentRoundTripMs, 25)]
    [InlineData(VoiceMetric.ReplyQueueWaitMs, 26)]
    [InlineData(VoiceMetric.SpeechEndToFirstAudioMs, 27)]
    [InlineData(VoiceMetric.SpeakerVerifyEarlyMs, 28)]
    public void VoiceMetric_TurnDecompositionValues_ArePinned(VoiceMetric metric, int expected)
    {
        // These decompose wake→first-audio. Values persist as ints in Redis; a renumber silently
        // re-labels historical data. SpeakerVerifyEarlyMs is the odd one out: the early mid-capture
        // pass runs WHILE the user is still speaking, so it overlaps the utterance and is deliberately
        // not part of the additive decomposition — it has its own member precisely so it can never be
        // blended into SpeakerVerifyMs by a grouping that isn't keyed on Outcome.
        ((int)metric).ShouldBe(expected);
    }

    [Theory]
    [InlineData(VoiceMetric.WakeSuppressed, 29)]
    [InlineData(VoiceMetric.WakeHandoff, 30)]
    public void VoiceMetric_ArbitrationValues_ArePinned(VoiceMetric metric, int expected)
    {
        // Values persist as ints in Redis; a renumber silently re-labels historical data.
        ((int)metric).ShouldBe(expected);
    }

    [Theory]
    [InlineData(VoiceDimension.SatelliteId, 0)]
    [InlineData(VoiceDimension.Room, 1)]
    [InlineData(VoiceDimension.Identity, 2)]
    [InlineData(VoiceDimension.Outcome, 3)]
    [InlineData(VoiceDimension.Priority, 4)]
    [InlineData(VoiceDimension.Speaker, 5)]
    [InlineData(VoiceDimension.Channel, 6)]
    public void VoiceDimension_HasPinnedWireValues(VoiceDimension dimension, int expected) =>
        ((int)dimension).ShouldBe(expected);
}