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
}