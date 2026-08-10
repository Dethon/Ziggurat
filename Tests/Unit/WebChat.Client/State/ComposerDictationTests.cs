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

    // Unchanged: while the reply runs, Send could only ever be dead.
    [Fact]
    public void WhileTheReplyRuns_TheControlIsStillCancel()
    {
        ComposerSelectors.SendControl(isStreaming: true, text: "", readyAttachments: 0, DictationStatus.Idle)
            .ShouldBe(SendControl.Cancel);
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