using Domain.Conversations;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class ConversationIdGeneratorTests
{
    [Fact]
    public void CreateFor_FormatsConversationIdAsChatColonThread()
    {
        var id = ConversationIdGenerator.CreateFor("topic-abc");

        id.ConversationId.ShouldBe($"{id.ChatId}:{id.ThreadId}");
        id.TopicId.ShouldBe("topic-abc");
    }

    [Fact]
    public void CreateFor_PinsHistoricalHashOutput_ForWireStableRedisIds()
    {
        // These chat/thread ids are persisted in Redis and must resolve identically forever. This
        // PR consolidated the previously-triplicated FNV hash into this single generator; pin the
        // exact output so any future change to the constants (init 0xcbf29ce484222325, prime
        // 0x100000001b3, seeds 0x1234/0x5678) fails loudly instead of silently re-keying every
        // historical conversation with a green suite.
        var id = ConversationIdGenerator.CreateFor("topic-abc");

        id.ChatId.ShouldBe(301919747930893025L);
        id.ThreadId.ShouldBe(1133305933L);
        id.ConversationId.ShouldBe("301919747930893025:1133305933");
    }

    [Fact]
    public void Create_GeneratesDistinctConversations()
    {
        var a = ConversationIdGenerator.Create();
        var b = ConversationIdGenerator.Create();

        a.ConversationId.ShouldNotBe(b.ConversationId);
    }
}