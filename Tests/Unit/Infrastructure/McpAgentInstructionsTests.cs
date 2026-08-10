using Domain.Prompts;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpAgentInstructionsTests
{
    [Fact]
    public void BuildInstructions_IncludesCurrentDate()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 10, 30, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "TestAgent",
            description: null,
            customInstructions: null,
            language: null,
            domainPrompts: [],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: fixedTime);

        result.ShouldContain("Today is Friday, 2026-05-15.");
    }

    // The date used to be the first line, which made the opening bytes of the system prompt
    // change every midnight and invalidated the whole cached prefix with them. It is the only
    // dated section, so it belongs after every static one: a day rollover then re-prefills the
    // tail instead of the ~28k-token prefix.
    [Fact]
    public void BuildInstructions_PutsTheDateAfterEveryStaticSection()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "TestAgent",
            description: null,
            customInstructions: null,
            language: null,
            domainPrompts: ["DOMAIN"],
            fileSystemPrompts: ["FS"],
            clientPrompts: ["CLIENT"],
            now: fixedTime);

        result.ShouldStartWith(BasePrompt.Instructions);
        var date = result.IndexOf("Today is", StringComparison.Ordinal);
        date.ShouldBeGreaterThan(result.IndexOf(BasePrompt.Instructions, StringComparison.Ordinal));
        date.ShouldBeGreaterThan(result.IndexOf("DOMAIN", StringComparison.Ordinal));
        date.ShouldBeGreaterThan(result.IndexOf("FS", StringComparison.Ordinal));
        date.ShouldBeGreaterThan(result.IndexOf("CLIENT", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstructions_PlacesCustomInstructionsLast()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "TestAgent",
            description: null,
            customInstructions: "CUSTOM",
            language: null,
            domainPrompts: ["DOMAIN"],
            fileSystemPrompts: ["FS"],
            clientPrompts: ["CLIENT"],
            now: fixedTime);

        // User custom instructions go last so they are the most recent guidance the
        // model sees, not buried at the top above the tool/MCP prompts.
        result.ShouldEndWith("CUSTOM");
        result.IndexOf("CUSTOM", StringComparison.Ordinal)
            .ShouldBeGreaterThan(result.IndexOf("CLIENT", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstructions_AppendsAllPromptSections()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "TestAgent",
            description: null,
            customInstructions: "CUSTOM",
            language: null,
            domainPrompts: ["DOMAIN"],
            fileSystemPrompts: ["FS"],
            clientPrompts: ["CLIENT"],
            now: fixedTime);

        result.ShouldContain("CUSTOM");
        result.ShouldContain("DOMAIN");
        result.ShouldContain("FS");
        result.ShouldContain("CLIENT");
        result.ShouldContain("Today is");
    }

    [Fact]
    public void BuildInstructions_IncludesAgentIdentity()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "Mycroft",
            description: "Voice assistant.",
            customInstructions: null,
            language: null,
            domainPrompts: [],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: fixedTime);

        result.ShouldContain("## Identity");
        result.ShouldContain("You are Mycroft. Voice assistant.");
    }

    [Fact]
    public void BuildInstructions_PlacesIdentityAfterBasePromptBeforeDomainPrompts()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "Mycroft",
            description: "Voice assistant.",
            customInstructions: null,
            language: null,
            domainPrompts: ["DOMAIN"],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: fixedTime);

        result.IndexOf(BasePrompt.Instructions, StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("## Identity", StringComparison.Ordinal));
        result.IndexOf("## Identity", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("DOMAIN", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstructions_BlankName_OmitsIdentityFragment()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "  ",
            description: "Voice assistant.",
            customInstructions: null,
            language: null,
            domainPrompts: [],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: fixedTime);

        result.ShouldNotContain("## Identity");
    }

    // The reply language is a hard output constraint, so it outranks even the custom
    // instructions: it is the last thing in the system prompt, closest to the conversation.
    [Fact]
    public void BuildInstructions_WithLanguage_PlacesLanguageSectionAfterCustomInstructions()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "Nabu",
            description: null,
            customInstructions: "CUSTOM",
            language: "es",
            domainPrompts: ["DOMAIN"],
            fileSystemPrompts: [],
            clientPrompts: ["CLIENT"],
            now: fixedTime);

        result.ShouldEndWith(LanguagePrompt.Build("es")!);
        result.IndexOf("## Idioma", StringComparison.Ordinal)
            .ShouldBeGreaterThan(result.IndexOf("CUSTOM", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildInstructions_NoLanguage_OmitsLanguageSection()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            name: "Nabu",
            description: null,
            customInstructions: "CUSTOM",
            language: "   ",
            domainPrompts: [],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: fixedTime);

        result.ShouldEndWith("CUSTOM");
        result.ShouldNotContain("## Idioma");
        result.ShouldNotContain("## Language");
    }
}