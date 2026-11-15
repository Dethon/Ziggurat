using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

// Which actions a bubble's menu offers: none while a reply streams or before the message is stored, and
// only what the hub would allow on this topic for this person.
public class MessageActionsLogicTests
{
    private static readonly ChatMessageModel Stored = new() { Role = "user", Content = "hola", EntryId = "e1" };

    private static readonly ChatMessageModel Reply = new() { Role = "assistant", Content = "buenas", EntryId = "e2" };

    [Fact]
    public void AStoredMessage_InATopicThePersonMayRewrite_OffersDelete() =>
        MessageActionsLogic.For(Stored, new MessageActionContext(Streaming: false, MayRewrite: true))
            .ShouldContain(MessageActionKind.Delete);

    [Fact]
    public void WhileAReplyStreams_NothingIsOffered() =>
        MessageActionsLogic.For(Stored, new MessageActionContext(Streaming: true, MayRewrite: true)).ShouldBeEmpty();

    [Fact]
    public void AMessageNotStoredYet_OffersNothing() =>
        MessageActionsLogic.For(Stored with { EntryId = null }, new MessageActionContext(false, true)).ShouldBeEmpty();

    // A topic read but not written in (another person's, seen through sees-all) or one another channel
    // goes on showing is not rewritten from here.
    [Fact]
    public void ATopicThePersonMayNotRewrite_OffersNoDelete() =>
        MessageActionsLogic.For(Stored, new MessageActionContext(false, MayRewrite: false))
            .ShouldNotContain(MessageActionKind.Delete);

    [Fact]
    public void APersonsMessage_OffersEdit() =>
        MessageActionsLogic.For(Stored, new MessageActionContext(false, true)).ShouldContain(MessageActionKind.Edit);

    [Fact]
    public void AReply_OffersEdit() =>
        MessageActionsLogic.For(Stored with { Role = "assistant" }, new MessageActionContext(false, true))
            .ShouldContain(MessageActionKind.Edit);

    [Fact]
    public void AnError_OffersNoEdit() =>
        MessageActionsLogic.For(Stored with { Role = "assistant", IsError = true }, new MessageActionContext(false, true))
            .ShouldNotContain(MessageActionKind.Edit);

    // Running a message again runs everything after it again: the tools every reply from there called.
    [Fact]
    public void ToolsFrom_AreEveryToolCalledFromThatMessageOn()
    {
        List<ChatMessageModel> messages =
        [
            new() { Role = "assistant", Content = "earlier", ToolsCalled = ["web_search"] },
            new() { Role = "user", Content = "hola", EntryId = "e1" },
            new() { Role = "assistant", Content = "set", ToolsCalled = ["timer_set"] },
            new() { Role = "user", Content = "again" },
            new() { Role = "assistant", Content = "printed", ToolsCalled = ["print", "timer_set"] }
        ];

        MessageActionsLogic.ToolsFrom(messages, 1).ShouldBe(["timer_set", "print"]);
    }

    [Fact]
    public void TheLastReply_OffersReroll_AndRerollOnAnotherModel()
    {
        var actions = MessageActionsLogic.For(Reply, new MessageActionContext(false, true, IsLastReply: true));

        actions.ShouldContain(MessageActionKind.Reroll);
        actions.ShouldContain(MessageActionKind.RerollWith);
    }

    [Fact]
    public void AnEarlierReply_OffersNoReroll() =>
        MessageActionsLogic.For(Reply, new MessageActionContext(false, true, IsLastReply: false))
            .ShouldNotContain(MessageActionKind.Reroll);

    // A failed turn has its Retry, which replaces it rather than keeping it as a version.
    [Fact]
    public void AFailedReply_OffersNoReroll() =>
        MessageActionsLogic.For(Reply with { IsError = true }, new MessageActionContext(false, true, IsLastReply: true))
            .ShouldNotContain(MessageActionKind.Reroll);

    // Forking reads a conversation and writes nothing of it: anyone who sees it may, another channel's
    // mirrored one and another person's seen through sees-all included.
    [Fact]
    public void ATopicThePersonMayOnlyRead_OffersFork() =>
        MessageActionsLogic.For(Stored, new MessageActionContext(false, MayRewrite: false))
            .ShouldBe([MessageActionKind.Fork]);
}