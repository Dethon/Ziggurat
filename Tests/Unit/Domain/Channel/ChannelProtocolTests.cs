using System.Text.Json;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

public class ChannelProtocolTests
{
    [Fact]
    public void ToArguments_WithSendReplyParams_ProducesCamelCaseKeysAndStringEnum()
    {
        var p = new SendReplyParams
        {
            ConversationId = "c1",
            Content = "hi",
            ContentType = ReplyContentType.Text,
            IsComplete = true,
            MessageId = "m1",
            TurnKey = "t1",
            AgentInitiated = false
        };

        var args = ChannelProtocol.ToArguments(p);

        args.Keys.OrderBy(k => k)
            .ShouldBe([
                "agentInitiated", "content", "contentType", "conversationId", "isComplete",
                "messageId", "turnKey"
            ]);
        JsonSerializer.Serialize(args["contentType"]).ShouldBe("\"Text\"");
    }

    [Fact]
    public void Deserialize_WithCamelCasePayload_ReadsTypedDto()
    {
        var element = JsonSerializer.Deserialize<JsonElement>(
            """{"conversationId":"c1","content":"hi","contentType":"Reasoning","isComplete":false,"messageId":null}""");

        var p = ChannelProtocol.Deserialize<SendReplyParams>(element);

        p.ShouldNotBeNull();
        p!.ConversationId.ShouldBe("c1");
        p.ContentType.ShouldBe(ReplyContentType.Reasoning);
        p.IsComplete.ShouldBeFalse();
        p.MessageId.ShouldBeNull();
    }

    [Fact]
    public void Serialize_DownloadCompletionNotification_RoundTripsWithStringEnumOrigin()
    {
        var payload = new ChannelMessageNotification
        {
            ConversationId = "conv-7",
            Sender = "fran",
            Content = "[download-complete] ...",
            AgentId = "jack",
            ReplyTo = [new ReplyTarget("signalr", "conv-7")],
            Origin = new MessageOrigin(MessageOriginKind.Download, null),
            Timestamp = DateTimeOffset.UtcNow
        };

        var element = JsonSerializer.SerializeToElement(payload, ChannelProtocol.SerializerOptions);
        var restored = ChannelProtocol.Deserialize<ChannelMessageNotification>(element).ShouldNotBeNull();

        restored.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Download, null));
        restored.ReplyTo.ShouldBe([new ReplyTarget("signalr", "conv-7")]);
        element.GetProperty("origin").GetProperty("kind").GetString().ShouldBe("Download");
    }

    [Fact]
    public void SerializerOptions_CanBeMarkedReadOnly_AsTheMcpSdkNotificationPathRequires()
    {
        // Regression: the MCP SDK's SendNotificationAsync calls JsonSerializerOptions.MakeReadOnly()
        // on the options it is handed, which throws when no TypeInfoResolver is set. Channel emitters
        // pass ChannelProtocol.SerializerOptions there, so without a resolver every channel/message
        // emit threw and was swallowed — the agent never saw inbound messages and never replied.
        var options = new JsonSerializerOptions(ChannelProtocol.SerializerOptions);

        Should.NotThrow(() => options.MakeReadOnly());
    }

    [Fact]
    public void Serialize_MessageNotificationWithConfigPatch_RoundTripsCamelCase()
    {
        var notification = new ChannelMessageNotification
        {
            ConversationId = "conv-1",
            Sender = "fran",
            Content = "hello",
            ConfigPatch = new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "high" }
        };

        var json = JsonSerializer.Serialize(notification, ChannelProtocol.SerializerOptions);
        var parsed = JsonSerializer.Deserialize<ChannelMessageNotification>(json, ChannelProtocol.SerializerOptions);

        json.ShouldContain("\"configPatch\"");
        parsed.ShouldNotBeNull();
        parsed.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "high" });
    }

    [Fact]
    public void Serialize_WidenedAgentCatalogEntry_RoundTrips()
    {
        var entry = new AgentCatalogEntry(
            "jack", "Jack", "Main agent",
            "openai/gpt-5.6-luna", "low",
            [new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
            AgentConfigPatch.SupportedEfforts);

        var json = JsonSerializer.Serialize(entry, ChannelProtocol.SerializerOptions);
        var parsed = JsonSerializer.Deserialize<AgentCatalogEntry>(json, ChannelProtocol.SerializerOptions);

        parsed.ShouldBe(entry with
        {
            PatchableModels = parsed!.PatchableModels,
            PatchableReasoningEfforts = parsed.PatchableReasoningEfforts
        });
        parsed.PatchableModels.ShouldBe([new PatchableModel("z-ai/glm-5.2", "GLM 5.2")]);
        parsed.PatchableReasoningEfforts.ShouldBe(AgentConfigPatch.SupportedEfforts);
    }
}