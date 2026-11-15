using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

// A conversation is the line the model and every page read, plus, for each place it branched, the
// versions put aside there. Rewriting it is a pure step from one to the next.
public class HistoryRewriteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PutAside_KeepsTheTailAsAVersionOfItsSlot_AndEndsTheLineThere()
    {
        var history = Line(("u1", "user"), ("a1", "assistant"));

        var after = Apply(history, new PutAside("u1", "a1"));

        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1"]);
        var slot = after.Versions["u1"];
        slot.Inactive.ShouldHaveSingleItem().Messages.Select(m => m.GetEntryId()).ShouldBe(["a1"]);
        slot.ActiveMadeAt.ShouldBe(Now);
    }

    [Fact]
    public void PutAside_FromTheRoot_PutsTheWholeLineAside()
    {
        var after = Apply(Line(("u1", "user"), ("a1", "assistant")), new PutAside(null, "a1"));

        after.Messages.ShouldBeEmpty();
        after.Versions[HistoryRewrite.Root].Inactive.ShouldHaveSingleItem().Messages.Count.ShouldBe(2);
    }

    [Fact]
    public void SwitchVersion_SwapsTheActiveTailWithTheOneChosen()
    {
        var history = Line(("u1", "user"), ("a1", "assistant"));
        var aside = Apply(history, new PutAside("u1", "a1"));
        var rerolled = aside with { Messages = [.. aside.Messages, Message("a2", "assistant")] };

        var after = Apply(rerolled, new SwitchVersion("u1", 0, "a2"));

        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1", "a1"]);
        after.Versions["u1"].Inactive.ShouldHaveSingleItem().Messages.Select(m => m.GetEntryId()).ShouldBe(["a2"]);
        after.PositionAt("u1").ShouldBe(new VersionPosition(0, 2));
    }

    // Versions further down a line put aside come back with it, since they are kept by the entries
    // they follow, which only that line holds.
    [Fact]
    public void ANestedSlot_ComesBackWithTheLineItWasIn()
    {
        var history = Line(("u1", "user"), ("a1", "assistant"), ("u2", "user"), ("a2", "assistant"));
        var nested = Apply(history, new PutAside("u2", "a2"));
        nested = nested with { Messages = [.. nested.Messages, Message("a2b", "assistant")] };
        var outer = Apply(nested, new PutAside("u1", "a2b"));
        outer = outer with { Messages = [.. outer.Messages, Message("a1b", "assistant")] };

        var back = Apply(outer, new SwitchVersion("u1", 0, "a1b"));

        back.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1", "a1", "u2", "a2b"]);
        back.Versions["u2"].Inactive.ShouldHaveSingleItem().Messages.Single().GetEntryId().ShouldBe("a2");
    }

    [Fact]
    public void CutFrom_RemovesTheMessageAndEverythingAfterIt()
    {
        var after = Apply(Line(("u1", "user"), ("a1", "assistant"), ("u2", "user"), ("a2", "assistant")),
            new CutFrom("u2", "a2"));

        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1", "a1"]);
    }

    // Deleting the version shown keeps the slot's others, and shows the newest of them.
    [Fact]
    public void CutFrom_InASlotWithOtherVersions_ShowsTheNewestRemaining()
    {
        var history = Line(("u1", "user"), ("a1", "assistant"));
        var once = Apply(history, new PutAside("u1", "a1"), Now);
        once = once with { Messages = [.. once.Messages, Message("a2", "assistant")] };
        var twice = Apply(once, new PutAside("u1", "a2"), Now.AddMinutes(1));
        twice = twice with { Messages = [.. twice.Messages, Message("a3", "assistant")] };

        var after = Apply(twice, new CutFrom("a3", "a3"));

        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1", "a2"]);
        after.Versions["u1"].Inactive.ShouldHaveSingleItem().Messages.Single().GetEntryId().ShouldBe("a1");
        after.PositionAt("u1").ShouldBe(new VersionPosition(1, 2));
    }

    [Fact]
    public void CutFrom_TakesTheVersionsNestedInWhatItRemoves()
    {
        var history = Line(("u1", "user"), ("a1", "assistant"), ("u2", "user"), ("a2", "assistant"));
        var nested = Apply(history, new PutAside("u2", "a2"));
        nested = nested with { Messages = [.. nested.Messages, Message("a2b", "assistant")] };
        var outer = Apply(nested, new PutAside("u1", "a2b"));

        var after = Apply(outer, new SwitchVersion("u1", 0, "u1"));
        after = Apply(after, new CutFrom("a1", "a2b"));

        after.Versions.ShouldBeEmpty();
        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1"]);
    }

    // A reply is drawn as one bubble over every message of its turn, its tool calls among them, so
    // deleting it takes them all, back to the person's message before it.
    [Fact]
    public void CutFrom_AReply_TakesItsWholeTurn()
    {
        var history = Line(("u1", "user"), ("call", "assistant"), ("result", "tool"), ("a1", "assistant"));

        var after = Apply(history, new CutFrom("a1", "a1"));

        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1"]);
    }

    // What left the conversation and is held nowhere else, so its file can go with it.
    [Fact]
    public void ARewrite_NamesTheAttachmentsNothingHoldsAnyMore()
    {
        var kept = Message("u1", "user");
        kept.SetAttachments([Attachment("c/kept"), Attachment("c/both")]);
        var dropped = Message("u2", "user");
        dropped.SetAttachments([Attachment("c/gone"), Attachment("c/both")]);
        var history = new ConversationHistory([kept, Message("a1", "assistant"), dropped, Message("a2", "assistant")],
            new Dictionary<string, SlotVersions>());

        var outcome = HistoryRewrite.Apply(history, new CutFrom("u2", "a2"), Now).ShouldBeOfType<Rewritten>();

        outcome.AttachmentsNoLongerHeld().ShouldBe(["c/gone"]);
    }

    // A version put aside still holds its files.
    [Fact]
    public void AnAttachmentInAVersionPutAside_IsStillHeld()
    {
        var withFile = Message("u2", "user");
        withFile.SetAttachments([Attachment("c/photo")]);
        var history = new ConversationHistory([Message("u1", "user"), Message("a1", "assistant"), withFile],
            new Dictionary<string, SlotVersions>());

        var outcome = HistoryRewrite.Apply(history, new PutAside("a1", "u2"), Now).ShouldBeOfType<Rewritten>();

        outcome.AttachmentsNoLongerHeld().ShouldBeEmpty();
    }

    // Edit and re-run: the person's message and what followed become a version of the slot before it,
    // and the line ends there for the edited words to be sent.
    [Fact]
    public void PutAsideFrom_APersonsMessage_KeepsItAndItsTailAsAVersionOfTheSlotBeforeIt()
    {
        var history = Line(("u1", "user"), ("a1", "assistant"), ("u2", "user"), ("a2", "assistant"));

        var after = Apply(history, new PutAsideFrom("u2", "a2"));

        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1", "a1"]);
        after.Versions["a1"].Inactive.Single().Messages.Select(m => m.GetEntryId()).ShouldBe(["u2", "a2"]);
    }

    // A reroll: the reply after a person's message is put aside as a version of the slot after it.
    [Fact]
    public void PutAsideReply_KeepsTheReplyAsAVersionOfTheSlotAfterThePersonsMessage()
    {
        var after = Apply(Line(("u1", "user"), ("a1", "assistant")), new PutAsideReply("u1", "a1"));

        after.Messages.Select(m => m.GetEntryId()).ShouldBe(["u1"]);
        after.Versions["u1"].Inactive.Single().Messages.Single().GetEntryId().ShouldBe("a1");
    }

    [Fact]
    public void PutAsideReply_ToAServicesTurn_IsNotRunAgain()
    {
        var fire = Message("fire", "user");
        fire.SetSenderId(ServiceSenders.Scheduler);
        var history = new ConversationHistory([fire, Message("a1", "assistant")], new Dictionary<string, SlotVersions>());

        HistoryRewrite.Apply(history, new PutAsideReply("fire", "a1"), Now)
            .ShouldBeOfType<RewriteRefused>().Reason.ShouldBe(RewriteRefusal.NotRerunnable);
    }

    [Fact]
    public void PutAsideFrom_TheFirstMessage_IsAVersionOfTheRoot()
    {
        var after = Apply(Line(("u1", "user"), ("a1", "assistant")), new PutAsideFrom("u1", "a1"));

        after.Messages.ShouldBeEmpty();
        after.Versions.Keys.ShouldBe([HistoryRewrite.Root]);
    }

    // Only a person's own words are run again: a reply is not a message anyone sent, and a turn a
    // service started acts for whoever its delegation names, which a page cannot be.
    [Theory]
    [InlineData("a1")]
    [InlineData("fire")]
    public void PutAsideFrom_AnythingButAPersonsMessage_IsNotRunAgain(string entryId)
    {
        var fire = Message("fire", "user");
        fire.SetSenderId(ServiceSenders.Scheduler);
        var history = new ConversationHistory(
            [Message("u1", "user"), Message("a1", "assistant"), fire, Message("a2", "assistant")],
            new Dictionary<string, SlotVersions>());

        HistoryRewrite.Apply(history, new PutAsideFrom(entryId, "a2"), Now)
            .ShouldBeOfType<RewriteRefused>().Reason.ShouldBe(RewriteRefusal.NotRerunnable);
    }

    // A message corrected in place: the old one and what followed it are a version of the slot before
    // it, and the line goes on from the corrected message with what followed copied under new names.
    [Fact]
    public void EditInPlace_APersonsMessage_KeepsTheOldLineAsAVersion_AndCopiesWhatFollowed()
    {
        var original = Message("u1", "user");
        original.SetTimestamp(Now.AddHours(-1));
        var history = new ConversationHistory([original, Message("a1", "assistant")], new Dictionary<string, SlotVersions>());

        var after = Apply(history, new EditInPlace("u1", "corrected", "a1"));

        after.Messages.Select(m => (m.Role.Value, m.Text)).ShouldBe([("user", "corrected"), ("assistant", "a1")]);
        after.Messages.Select(m => m.GetEntryId()).ShouldNotContain("u1");
        after.Messages.Select(m => m.GetEntryId()).ShouldNotContain("a1");
        after.Messages[0].GetTimestamp().ShouldBe(Now.AddHours(-1));
        after.Versions[HistoryRewrite.Root].Inactive.Single().Messages.Select(m => m.GetEntryId()).ShouldBe(["u1", "a1"]);
        after.PositionAt(HistoryRewrite.Root).ShouldBe(new VersionPosition(1, 2));
    }

    // A reply is one bubble over its turn: its corrected text is the turn's last word, the tool calls
    // and their results stay paired, and what the model said along the way goes.
    [Fact]
    public void EditInPlace_AReply_PutsTheTextInItsLastMessage_AndKeepsItsToolCalls()
    {
        var thinking = new ChatMessage(ChatRole.Assistant,
            [new TextContent("let me look"), new FunctionCallContent("c1", "lookup")]);
        thinking.SetEntryId("a0");
        var result = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "found")]);
        result.SetEntryId("t0");
        var history = new ConversationHistory(
            [Message("u1", "user"), thinking, result, Message("a1", "assistant"), Message("u2", "user")],
            new Dictionary<string, SlotVersions>());

        var after = Apply(history, new EditInPlace("a1", "the right answer", "u2"));

        after.Messages.Select(m => m.Role.Value).ShouldBe(["user", "assistant", "tool", "assistant", "user"]);
        after.Messages[1].Contents.OfType<TextContent>().ShouldBeEmpty();
        after.Messages[1].Contents.OfType<FunctionCallContent>().ShouldHaveSingleItem();
        after.Messages[3].Text.ShouldBe("the right answer");
        after.Messages[4].Text.ShouldBe("u2");
        after.Versions["u1"].Inactive.Single().Messages.Count.ShouldBe(4);
    }

    [Fact]
    public void ARewrite_OfALineThatChangedSinceThePageSawIt_IsRefused()
    {
        var outcome = HistoryRewrite.Apply(Line(("u1", "user"), ("a1", "assistant")), new CutFrom("u1", "older"), Now);

        outcome.ShouldBeOfType<RewriteRefused>().Reason.ShouldBe(RewriteRefusal.Changed);
    }

    [Fact]
    public void ARewrite_NamingNoMessageOfTheLine_IsRefused()
    {
        var outcome = HistoryRewrite.Apply(Line(("u1", "user"), ("a1", "assistant")), new CutFrom("gone", "a1"), Now);

        outcome.ShouldBeOfType<RewriteRefused>().Reason.ShouldBe(RewriteRefusal.NotFound);
    }

    // A line kept before entry ids is named by place and words; the rewrite stamps real ids on it,
    // so what it puts aside can be found again.
    [Fact]
    public void ALegacyLine_IsStampedWithEntryIds_AsItIsRewritten()
    {
        var legacy = new ConversationHistory(
            [new ChatMessage(ChatRole.User, "hola"), new ChatMessage(ChatRole.Assistant, "buenas")],
            new Dictionary<string, SlotVersions>());
        var user = HistoryEntries.IdAt(legacy.Messages, 0);
        var last = HistoryEntries.IdAt(legacy.Messages, 1);

        var after = Apply(legacy, new PutAside(user, last));

        var stamped = after.Messages.Single().GetEntryId();
        stamped.ShouldNotBeNull();
        stamped.ShouldNotStartWith("legacy:");
        after.Versions[stamped].Inactive.Single().Messages.Single().GetEntryId().ShouldNotBeNull();
    }

    private static ConversationHistory Apply(ConversationHistory history, HistoryRewrite rewrite, DateTimeOffset? now = null) =>
        HistoryRewrite.Apply(history, rewrite, now ?? Now).ShouldBeOfType<Rewritten>().After;

    private static AttachmentReference Attachment(string id) =>
        new() { Id = id, FileName = "f.png", MediaType = "image/png", SizeBytes = 1 };

    private static ConversationHistory Line(params (string Id, string Role)[] messages) =>
        new([.. messages.Select(m => Message(m.Id, m.Role))], new Dictionary<string, SlotVersions>());

    private static ChatMessage Message(string id, string role)
    {
        var message = new ChatMessage(new ChatRole(role), id);
        message.SetEntryId(id);
        return message;
    }
}