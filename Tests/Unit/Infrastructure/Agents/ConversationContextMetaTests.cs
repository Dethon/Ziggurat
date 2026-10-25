using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.DTOs.Channel;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class ConversationContextMetaTests
{
    private static readonly ConversationContext _context = new(
        "jack", "conv-9", "fran", new ReplyTarget("signalr", "conv-9"));

    [Fact]
    public void TryBuild_WithContextInOptions_ProducesMetaJson()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationContextMeta.OptionsKey] = _context
            }
        };

        var meta = ConversationContextMeta.TryBuild(options).ShouldNotBeNull();

        var node = meta[ConversationContextMeta.MetaKey].ShouldNotBeNull();
        node["conversationId"]!.GetValue<string>().ShouldBe("conv-9");
        node["agentId"]!.GetValue<string>().ShouldBe("jack");
        node["userId"]!.GetValue<string>().ShouldBe("fran");
        node["origin"]!["channelId"]!.GetValue<string>().ShouldBe("signalr");
    }

    [Fact]
    public void Build_AFiresContext_SpellsItsOriginKind()
    {
        var meta = ConversationContextMeta.Build(_context with { OriginKind = MessageOriginKind.Schedule })
            .ShouldNotBeNull();

        meta[ConversationContextMeta.MetaKey]!["originKind"]!.GetValue<string>().ShouldBe("Schedule");
        ConversationScope.Parse(meta)!.OriginKind.ShouldBe(MessageOriginKind.Schedule);
    }

    // The field is optional both ways: a sender from before it sends none, and a person's own
    // turn reads back as no origin at all.
    [Fact]
    public void Parse_AContextWithoutAnOriginKind_ReadsAsAPersonsTurn()
    {
        var meta = JsonNode.Parse(
            """
            { "com.herfluffness/conversationContext": {
                "agentId": "jack", "conversationId": "conv-9", "userId": "fran",
                "origin": { "channelId": "signalr", "conversationId": "conv-9" } } }
            """)!.AsObject();

        var context = ConversationScope.Parse(meta).ShouldNotBeNull();

        context.OriginKind.ShouldBeNull();
        context.UserId.ShouldBe("fran");
    }

    [Fact]
    public void TryBuild_NullOptionsOrMissingKey_ReturnsNull()
    {
        ConversationContextMeta.TryBuild(null).ShouldBeNull();
        ConversationContextMeta.TryBuild(new ChatOptions()).ShouldBeNull();
    }

    [Fact]
    public void MetaKey_UsesAVendorPrefix()
    {
        // The 2026-07-28 spec reserves any _meta prefix whose second label is "mcp" or
        // "modelcontextprotocol", and recommends reverse-DNS for everything else. A bare key
        // sits in the same namespace as progressToken/traceparent and could be claimed later.
        ChannelProtocol.ConversationContextMetaKey.ShouldBe("com.herfluffness/conversationContext");
    }
}