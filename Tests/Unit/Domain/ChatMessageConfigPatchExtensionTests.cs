using System.Text.Json;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatMessageConfigPatchExtensionTests
{
    [Fact]
    public void GetConfigPatch_FromJsonElement_Deserializes()
    {
        var message = new ChatMessage(ChatRole.User, "hi")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["ConfigPatch"] = JsonSerializer.SerializeToElement(
                    new AgentConfigPatch { Model = "z-ai/glm-5.2" }, ChannelProtocol.SerializerOptions)
            }
        };

        message.GetConfigPatch().ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }

    [Fact]
    public void SetConfigPatch_Null_LeavesPropertiesUntouched()
    {
        var message = new ChatMessage(ChatRole.User, "hi");

        message.SetConfigPatch(null);

        message.AdditionalProperties.ShouldBeNull();
    }

    // What a history read hands back: the persisted message's format as a JSON element.
    [Fact]
    public void GetResponseFormat_FromJsonElement_Deserializes()
    {
        var schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{}}""");
        var message = new ChatMessage(ChatRole.User, "hi")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["ResponseFormat"] = JsonSerializer.SerializeToElement(
                    new ResponseFormat { Name = "meal_estimate", Schema = schema }, ChannelProtocol.SerializerOptions)
            }
        };

        var format = message.GetResponseFormat();

        format.ShouldNotBeNull();
        format.Name.ShouldBe("meal_estimate");
        JsonElement.DeepEquals(format.Schema, schema).ShouldBeTrue();
    }

    [Fact]
    public void SetResponseFormat_Null_LeavesPropertiesUntouched()
    {
        var message = new ChatMessage(ChatRole.User, "hi");

        message.SetResponseFormat(null);

        message.AdditionalProperties.ShouldBeNull();
    }
}