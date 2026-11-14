using Domain.DTOs;
using Shouldly;
using WebChat.Client.Components.Admin;

namespace Tests.Unit.WebChat.Client.Components;

// An agent's whole definition as an owner reads it on the agents page: indented JSON in the casing
// its seeded form is written in, with what the definition leaves unset left out rather than shown null.
public sealed class AgentDefinitionTextTests
{
    [Fact]
    public void ADefinition_IsIndentedCamelCaseJson_WithoutItsUnsetFields()
    {
        var text = AgentDefinitionText.Of(new AgentDefinition
        {
            Id = "nabu", Name = "Nabu", Model = "deepseek/deepseek-v4.1-flash-1017", McpServerEndpoints = ["vault"],
            PromptSections = ["voice"], Language = "es"
        });

        text.ShouldContain("\n  \"id\": \"nabu\"");
        text.ShouldContain("\"promptSections\": [");
        text.ShouldContain("\"language\": \"es\"");
        text.ShouldNotContain("null");
        text.ShouldNotContain("customInstructions");
    }
}