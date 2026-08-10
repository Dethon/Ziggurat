using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Channels;

public class ConversationScopeTests
{
    private static readonly ConversationContext _context = new(
        AgentId: "agent-1",
        ConversationId: "conv-abc",
        UserId: "user-42",
        Origin: new ReplyTarget("signalr", "conv-abc"));

    private static JsonObject MetaFor(ConversationContext context) => new()
    {
        [ChannelProtocol.ConversationContextMetaKey] =
            JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
    };

    [Fact]
    public void Build_JoinsAgentAndConversation()
    {
        ConversationScope.Build("nabu", "conv-a").ShouldBe("nabu:conv-a");
    }

    [Fact]
    public void Parse_NullMeta_ReturnsNull()
    {
        ConversationScope.Parse(null).ShouldBeNull();
    }

    [Fact]
    public void Parse_MetaWithoutConversationContextKey_ReturnsNull()
    {
        ConversationScope.Parse(new JsonObject { ["other"] = "value" }).ShouldBeNull();
    }

    [Fact]
    public void Parse_RoundTripsConversationContext()
    {
        var result = ConversationScope.Parse(MetaFor(_context)).ShouldNotBeNull();

        result.AgentId.ShouldBe("agent-1");
        result.ConversationId.ShouldBe("conv-abc");
        result.UserId.ShouldBe("user-42");
        result.Origin.ChannelId.ShouldBe("signalr");
        result.Origin.ConversationId.ShouldBe("conv-abc");
    }

    [Fact]
    public void TryResolve_WithContext_ReturnsAgentAndConversationScope()
    {
        ConversationScope.TryResolve(MetaFor(_context), out var scope).ShouldBeTrue();
        scope.ShouldBe("agent-1:conv-abc");
    }

    [Fact]
    public void TryResolve_WithoutContext_ReturnsFalseAndEmptyScope()
    {
        ConversationScope.TryResolve(null, out var scope).ShouldBeFalse();
        scope.ShouldBeEmpty();

        ConversationScope.TryResolve(new JsonObject { ["other"] = "value" }, out var other).ShouldBeFalse();
        other.ShouldBeEmpty();
    }
}