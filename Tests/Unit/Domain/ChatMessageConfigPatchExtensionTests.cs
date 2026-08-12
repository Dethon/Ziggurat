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
}