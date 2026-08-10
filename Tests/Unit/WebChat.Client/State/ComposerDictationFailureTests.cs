using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Components;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// Every way a dictation can end badly, answered in plain words rather than by nothing happening.
public sealed class ComposerDictationFailureTests
{
    [Fact]
    public async Task ARefusedPermission_ShowsARefusalAndStopsTheControlTryingForTheSession()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(
            new DictationUnavailable("I cannot use the microphone here: permission was refused."));

        var dictation = client.Composer.State.Dictation;
        dictation.Unavailable.ShouldBeTrue();
        dictation.Refusal.ShouldNotBeNull().ShouldContain("permission was refused");
        dictation.Status.ShouldBe(DictationStatus.Idle);
    }

    [Fact]
    public async Task AFailedTranscription_ShowsTheRefusalAndLandsNoPartialText()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationStarted());
        client.Dispatcher.Dispatch(new DictationEnded());
        client.Dispatcher.Dispatch(new DictationFailed("I could not turn that recording into words."));

        var dictation = client.Composer.State.Dictation;
        dictation.Status.ShouldBe(DictationStatus.Idle);
        dictation.Refusal.ShouldNotBeNull();
        dictation.Transcript.ShouldBeNull();
        // A failure is about this recording, not about the microphone: the control keeps working.
        dictation.Unavailable.ShouldBeFalse();
    }

    // The refusal is about a recording that no longer exists, so the next one clears it rather
    // than leaving a stale complaint over a live microphone.
    [Fact]
    public async Task StartingAgain_ClearsTheLastRefusal()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationFailed("boom"));
        client.Dispatcher.Dispatch(new DictationStarted());

        client.Composer.State.Dictation.Refusal.ShouldBeNull();
    }

    // The same notion of busy an upload in flight already had: the half-message the transcript was
    // meant to complete must not be able to go out without it.
    [Fact]
    public void WhileATranscriptIsInFlight_SendIsUnavailable()
    {
        ChatInputLogic.CanSend(
            disabled: false, inputText: "half a thought", isStreaming: false,
            readyAttachments: 0, uploadInFlight: true).ShouldBeFalse();

        ChatInputLogic.CanSend(
            disabled: false, inputText: "half a thought", isStreaming: false,
            readyAttachments: 0, uploadInFlight: false).ShouldBeTrue();
    }

    // Words meant for one conversation must never surface in another.
    [Fact]
    public async Task SwitchingTopicMidDictation_StopsTheMicrophone()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationStarted());
        client.Dispatcher.Dispatch(new SelectTopic("topic-2"));

        await TestChat.Eventually(() => client.Dictation.Discards == 1);
    }

    [Fact]
    public async Task SwitchingTopicWithNoDictationRunning_AsksTheMicrophoneNothing()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new SelectTopic("topic-2"));

        await Task.Delay(50);
        client.Dictation.Discards.ShouldBe(0);
    }

    // The live connection matters when the ticket is minted and when the request is made, and
    // nowhere else: recording through a network gap is fine, and the failure is the ordinary one.
    [Fact]
    public async Task WithNoLiveConnection_NoTicketIsHandedOverAndTheBrowserSaysSo()
    {
        await using var client = await StartAsync();
        client.GoNotLive();

        var upload = await client.Service<DictationEffect>().MintTicketAsync();

        upload.ShouldBeNull();
    }

    // The latched dictation's two buttons: one throws the recording away with no confirmation, the
    // other ends it and fills the composer. Neither of them sends.
    [Fact]
    public async Task TheTrashButton_ThrowsTheRecordingAway()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationLatched());
        client.Dispatcher.Dispatch(new DiscardDictation());

        await TestChat.Eventually(() => client.Dictation.Discards == 1);
        client.Dictation.Stops.ShouldBe(0);
    }

    [Fact]
    public async Task TheStopButton_EndsTheDictationAndSendsNothing()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationLatched());
        client.Dispatcher.Dispatch(new StopDictation());

        await TestChat.Eventually(() => client.Dictation.Stops == 1);
        client.Transport.Calls.ShouldNotContain(call => call.MethodName == "SendMessage");
    }

    private static async Task<ScriptedChatClient> StartAsync()
    {
        var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new AddTopic(new StoredTopic
        {
            TopicId = "topic-1",
            ChatId = 7,
            ThreadId = 42,
            AgentId = "agent-1",
            Name = "Chat",
            CreatedAt = DateTime.UtcNow
        }));
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));
        return client;
    }
}