using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

public class ConversationContextStampingTests
{
    private static readonly ConversationContext _context = new(
        "jonas", "conv-7", "fran", new ReplyTarget("signalr", "conv-7"));

    [Fact]
    public void Get_AfterJsonRoundTrip_DeserializesContext()
    {
        // Chat history persists AdditionalProperties as JSON; on restore the value
        // comes back as a JsonElement, not the original record.
        var message = new ChatMessage(ChatRole.User, "hi");
        message.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["ConversationContext"] = System.Text.Json.JsonSerializer.SerializeToElement(
                _context, ChannelProtocol.SerializerOptions)
        };
        message.GetConversationContext().ShouldBe(_context);
    }

    [Fact]
    public void Get_WhenUnset_ReturnsNull()
    {
        new ChatMessage(ChatRole.User, "hi").GetConversationContext().ShouldBeNull();
    }
}