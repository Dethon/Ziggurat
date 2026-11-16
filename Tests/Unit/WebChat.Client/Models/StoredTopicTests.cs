using Shouldly;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Models;

// The rename and delete controls are drawn by the rule the hub refuses by (TopicViewer.MayRenameOrDelete):
// a control the hub will refuse is one that silently does nothing, which is what an owner met on a
// member's shared topic.
public class StoredTopicTests
{
    [Fact]
    public void ATopicOfMyOwn_IsMineToRenameOrDelete() =>
        new StoredTopic { OwnerId = "me" }.MayRenameOrDelete("me").ShouldBeTrue();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SomeoneElsesTopic_SharedOrNot_IsNotMineToRenameOrDelete(bool shared) =>
        new StoredTopic { OwnerId = "pablo", Shared = shared }.MayRenameOrDelete("elena").ShouldBeFalse();

    [Fact]
    public void ATopicNobodyOwns_IsAnyViewersToRenameOrDelete() =>
        new StoredTopic { OwnerId = null }.MayRenameOrDelete("elena").ShouldBeTrue();

    [Fact]
    public void NobodySignedIn_MayRenameOrDeleteNothingOwned() =>
        new StoredTopic { OwnerId = "pablo" }.MayRenameOrDelete(null).ShouldBeFalse();

    // Writing in a topic is the hub's MayContinue: the owner's, a shared one's for the household, and
    // one nobody owns for whoever sees it. Seeing all conversations is reading them, so another
    // person's private topic is read-only, and the composer says so rather than taking a message the
    // hub refuses.
    [Theory]
    [InlineData("me", false, "me", true)]
    [InlineData("pablo", true, "elena", true)]
    [InlineData(null, false, "elena", true)]
    [InlineData("pablo", false, "elena", false)]
    [InlineData("pablo", false, null, false)]
    public void MayContinue_IsTheHubsRule(string? owner, bool shared, string? viewer, bool expected) =>
        new StoredTopic { OwnerId = owner, Shared = shared }.MayContinue(viewer).ShouldBe(expected);

    // Rewriting is continuing, on a topic over its own conversation that no other channel goes on
    // showing (TopicMirrors): the hub refuses the rest, and a control it refuses does nothing.
    [Fact]
    public void MyOwnChatClientTopic_IsMineToRewrite() =>
        Own("t").MayRewrite("me").ShouldBeTrue();

    [Fact]
    public void AMirroredTopic_IsNotRewritten()
    {
        var topic = Own("t");
        topic.Mirrored = true;

        topic.MayRewrite("me").ShouldBeFalse();
    }

    [Fact]
    public void ATopicOverAnotherConversation_IsNotRewritten()
    {
        var topic = Own("t");
        topic.ChatId = 1;

        topic.MayRewrite("me").ShouldBeFalse();
    }

    [Fact]
    public void AnotherPersonsPrivateTopic_IsNotMineToRewrite()
    {
        var topic = Own("t");
        topic.OwnerId = "pablo";

        topic.MayRewrite("me").ShouldBeFalse();
    }

    private static StoredTopic Own(string topicId)
    {
        var id = global::Domain.Conversations.ConversationIdGenerator.CreateFor(topicId);
        return new StoredTopic { TopicId = topicId, ChatId = id.ChatId, ThreadId = id.ThreadId, OwnerId = "me" };
    }
}