using Shouldly;
using WebChat.Client.Components;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

// Who a person's message in a topic is drawn as. The page knows two people by name without asking
// anyone: whoever is signed in, and whoever started the topic (its owner's name, stamped when it
// began). A person reading somebody else's topic — an owner who sees every conversation, a shared
// topic — used to see that person drawn as the first letter of their account id.
public sealed class SenderLabelTests
{
    private static readonly SignedInPerson _lucia = new("aaaa", "Lucía", "/avatars/lucia.png");

    [Fact]
    public void TheSignedInPersonsMessage_IsDrawnAsThem()
    {
        SenderLabel.Of("aaaa", _lucia, ownerId: "bbbb", ownerName: "Pablo")
            .ShouldBe(("/avatars/lucia.png", "Lucía"));
    }

    [Fact]
    public void TheTopicOwnersMessage_ReadBySomebodyElse_IsDrawnAsTheOwnersName()
    {
        SenderLabel.Of("bbbb", _lucia, ownerId: "bbbb", ownerName: "Pablo")
            .ShouldBe(((string?)null, "Pablo"));
    }

    [Fact]
    public void AMessageFromSomebodyThePageCannotName_KeepsTheIdTheHubStamped()
    {
        SenderLabel.Of("cccc", _lucia, ownerId: "bbbb", ownerName: "Pablo")
            .ShouldBe(((string?)null, "cccc"));
    }

    [Fact]
    public void AMessageWithNoSender_IsAnonymous()
    {
        SenderLabel.Of(null, _lucia, ownerId: null, ownerName: null)
            .ShouldBe(((string?)null, "Anonymous"));
    }
}