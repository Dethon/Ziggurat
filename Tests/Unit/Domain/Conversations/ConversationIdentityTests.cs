using Domain.Conversations;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class ConversationIdentityTests
{
    [Fact]
    public void Parse_InvertsWhatConversationIdRendered()
    {
        var identity = new ConversationIdentity(301919747930893025L, 1133305933L);

        identity.ConversationId.ShouldBe("301919747930893025:1133305933");
        ConversationIdentity.Parse(identity.ConversationId).ShouldBe(identity);
    }

    // Telegram supergroup chat ids are negative; the spelling must survive them.
    [Fact]
    public void Parse_RoundTripsANegativeChatId()
    {
        var identity = new ConversationIdentity(-1001234567890L, 42L);

        ConversationIdentity.Parse(identity.ConversationId).ShouldBe(identity);
    }

    [Fact]
    public void Parse_RoundTripsTheNonForumSpelling()
    {
        var identity = ConversationIdentity.ForChat(100L, messageThreadId: null);

        identity.ConversationId.ShouldBe("100:100");
        ConversationIdentity.Parse("100:100").ShouldBe(identity);
    }

    // An equal thread and chat is the non-forum spelling: there is no thread to address.
    [Fact]
    public void MessageThreadId_IsNothingWhereThreadEqualsChat()
    {
        new ConversationIdentity(100L, 100L).MessageThreadId.ShouldBeNull();
        new ConversationIdentity(100L, 42L).MessageThreadId.ShouldBe(42L);
    }

    [Fact]
    public void ForChat_StoresTheChatIdInTheThreadSlotWhereThereIsNoThread()
    {
        ConversationIdentity.ForChat(100L, 42L).ShouldBe(new ConversationIdentity(100L, 42L));
        ConversationIdentity.ForChat(100L, null).ShouldBe(new ConversationIdentity(100L, 100L));
    }

    // A value that does not parse as a conversation identity is some other channel's address
    // passing through, not a broken identity.
    [Theory]
    [InlineData("kitchen-satellite")]
    [InlineData("0e984725c6f84e2bb7d5f9a1c8f3b2a1")]
    [InlineData("eval:301919747930893025:1133305933")]
    [InlineData("560:7:9")]
    [InlineData("560:")]
    [InlineData(":7")]
    [InlineData("")]
    [InlineData("560: 7")]
    [InlineData("560:+7")]
    public void Parse_YieldsNothingForSomeOtherChannelsAddress(string value)
    {
        ConversationIdentity.Parse(value).ShouldBeNull();
    }

    [Fact]
    public void Parse_YieldsNothingForACorrelationGuid()
    {
        ConversationIdentity.Parse(Guid.NewGuid().ToString()).ShouldBeNull();
    }
}