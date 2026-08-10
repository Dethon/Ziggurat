using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// A dictation is one run of the microphone that ends as words. The browser owns the microphone,
// the encoder and the gesture; everything here is what the client does with what the browser
// reports back — which control the composer offers, and where the words land.
public sealed class ComposerDictationTests
{
    [Fact]
    public void WithNothingTypedAndNothingAttached_TheRightHandControlIsTheMicrophone()
    {
        ComposerSelectors.SendControl(isStreaming: false, text: "", readyAttachments: 0, DictationStatus.Idle)
            .ShouldBe(SendControl.Microphone);
    }

    [Fact]
    public void AsSoonAsThereIsText_TheControlIsSendAgain()
    {
        ComposerSelectors.SendControl(isStreaming: false, text: "h", readyAttachments: 0, DictationStatus.Idle)
            .ShouldBe(SendControl.Send);
    }

    // A photo with no caption is a normal thing to send, so a ready attachment is enough to make
    // the control the one that sends it.
    [Fact]
    public void WithAFileAttachedAndNothingTyped_TheControlIsSend()
    {
        ComposerSelectors.SendControl(isStreaming: false, text: "  ", readyAttachments: 1, DictationStatus.Idle)
            .ShouldBe(SendControl.Send);
    }

    // Unchanged: while the reply runs, Send could only ever be dead — and that is true whatever
    // the microphone is doing, which is what "the streaming-cancel state continues to take
    // precedence" means.
    [Theory]
    [InlineData(DictationStatus.Idle)]
    [InlineData(DictationStatus.Recording)]
    [InlineData(DictationStatus.Latched)]
    [InlineData(DictationStatus.Transcribing)]
    public void WhileTheReplyRuns_TheControlIsStillCancel(DictationStatus dictation)
    {
        ComposerSelectors.SendControl(isStreaming: true, text: "", readyAttachments: 0, dictation)
            .ShouldBe(SendControl.Cancel);
    }

    // An open microphone holds the spot even against text: the strip is what is on screen, and the
    // control must not change under the finger holding it.
    [Theory]
    [InlineData(DictationStatus.Recording)]
    [InlineData(DictationStatus.Latched)]
    public void WhileTheMicrophoneIsOpen_TheControlStaysTheMicrophone(DictationStatus dictation)
    {
        ComposerSelectors.SendControl(isStreaming: false, text: "half typed", readyAttachments: 1, dictation)
            .ShouldBe(SendControl.Microphone);
    }

    // Once the recording is over the composer is ordinary again: the words that are already in it
    // are what the control is for.
    [Fact]
    public void WhileTheTranscriptIsStillComing_TextInTheBoxMakesTheControlSendAgain()
    {
        ComposerSelectors.SendControl(
                isStreaming: false, text: "half typed", readyAttachments: 0, DictationStatus.Transcribing)
            .ShouldBe(SendControl.Send);
    }

    [Fact]
    public void TheTranscript_IsAppendedToWhatWasAlreadyTyped()
    {
        ComposerSelectors.Append("half a thought", "and the rest of it")
            .ShouldBe("half a thought and the rest of it");
    }

    [Fact]
    public void TheTranscript_IsTheWholeComposerWhenNothingWasTyped()
    {
        ComposerSelectors.Append("", "the whole thought").ShouldBe("the whole thought");
        ComposerSelectors.Append("   ", "the whole thought").ShouldBe("the whole thought");
    }

    [Fact]
    public async Task HoldingTheMicrophone_PutsTheComposerIntoRecording()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationStarted());

        client.Composer.State.Dictation.Status.ShouldBe(DictationStatus.Recording);
    }

    [Fact]
    public async Task Releasing_LeavesTheComposerTranscribingUntilTheWordsArrive()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationStarted());
        client.Dispatcher.Dispatch(new DictationEnded());

        client.Composer.State.Dictation.Status.ShouldBe(DictationStatus.Transcribing);

        client.Dispatcher.Dispatch(new DictationTranscribed("pon el temporizador"));

        client.Composer.State.Dictation.Status.ShouldBe(DictationStatus.Idle);
        client.Composer.State.Dictation.Transcript!.Text.ShouldBe("pon el temporizador");
    }

    // Two dictations of the same words are two events: the composer has to take the second one
    // as well, so the transcript carries a stamp rather than only its text.
    [Fact]
    public async Task TheSameWordsDictatedTwice_ArriveTwice()
    {
        await using var client = await StartAsync();

        client.Dispatcher.Dispatch(new DictationTranscribed("otra vez"));
        var first = client.Composer.State.Dictation.Transcript!;

        client.Dispatcher.Dispatch(new DictationTranscribed("otra vez"));
        var second = client.Composer.State.Dictation.Transcript!;

        second.ShouldNotBe(first);
        second.Text.ShouldBe("otra vez");
    }

    // The browser mints the ticket and posts the audio; the ticket is minted here because only
    // the live connection can, and the URL is the one the upload store already resolves to.
    [Fact]
    public async Task TheBrowserAsksForATicket_AndGetsOneScopedToTheSpace()
    {
        await using var client = await StartAsync();

        var upload = await client.Service<DictationEffect>().MintTicketAsync();

        upload.ShouldNotBeNull();
        upload.Token.ShouldBe("dictation-ticket-1");
        // The rules ride with it, from the same live call.
        upload.MaxMs.ShouldBe(120_000);
        upload.MinMs.ShouldBe(400);
        upload.Url.ShouldContain("/api/dictation");
        upload.Url.ShouldContain("space=default");
    }

    // The microphone is registered with what the server said it is allowed to record, so the
    // browser never carries a cap of its own and changing one needs no client deploy.
    [Fact]
    public async Task TheBrowserIsToldTheCapAndTheFloorTheServerConfigured()
    {
        await using var client = await StartAsync();

        await client.Service<DictationEffect>().RegisterAsync(default);

        client.Dictation.Limits.ShouldNotBeNull();
        client.Dictation.Limits!.MaxMs.ShouldBe(120_000);
        client.Dictation.Limits!.MinMs.ShouldBe(400);
    }

    // The limits need a live connection, so they routinely arrive after the first render has
    // already registered the microphone. A cap the browser learned once and never revised is a cap
    // that needs a client deploy to change, which is the thing the limits call exists to avoid.
    [Fact]
    public async Task LimitsThatArriveAfterTheMicrophoneWasRegistered_StillReachTheBrowser()
    {
        await using var client = await StartAsync();
        await client.Service<DictationEffect>().RegisterAsync(default);

        client.Dispatcher.Dispatch(new AttachmentLimitsLoaded(
            new AttachmentLimits(1024, 1, [], MaxDictationMs: 4_000, MinDictationMs: 250)));

        await TestChat.Eventually(() => client.Dictation.Limits!.MaxMs == 4_000);
        client.Dictation.Limits!.MinMs.ShouldBe(250);
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