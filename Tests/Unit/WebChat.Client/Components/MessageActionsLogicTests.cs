using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

// Which actions a bubble's menu offers: none while a reply streams or before the message is stored, and
// only what the hub would allow on this topic for this person.
public class MessageActionsLogicTests
{
    private static readonly ChatMessageModel Stored = new() { Role = "user", Content = "hola", EntryId = "e1" };

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
}