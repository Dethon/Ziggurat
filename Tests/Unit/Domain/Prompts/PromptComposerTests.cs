using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// The ordering rules that used to live as comments beside a chain of Prepend and Append calls.
// They are now bands on a declaration, so these drive the composer rather than the agent — same
// rules, one layer down, and reachable without building a chat client.
public class PromptComposerTests
{
    private static readonly DateTimeOffset _fixedTime = new(2026, 5, 15, 10, 30, 0, TimeSpan.Zero);

    // Placeholders bound to real declarations, because an undeclared section is assembled in the
    // client band whatever it was meant to be — a test using invented names would be testing the
    // fallback rather than the ordering.
    private static readonly PromptSection _domain = PromptManifest.Bind(PromptManifest.Subagents, "DOMAIN");
    private static readonly PromptSection _fileSystem =
        PromptManifest.Bind(PromptManifest.FilesystemMounts, "FS");
    private static readonly PromptSection _client = PromptManifest.Bind(SchedulingPrompt.Name, "CLIENT");

    private static PromptContext Context(
        string name = "TestAgent",
        string? description = null,
        string? customInstructions = null,
        string? language = null,
        bool withSections = false) => new()
        {
            AgentId = "test-agent",
            Name = name,
            Description = description,
            Domain = withSections ? [_domain] : [],
            FileSystem = withSections ? [_fileSystem] : [],
            Client = withSections ? [_client] : [],
            CustomInstructions = customInstructions,
            Language = language,
            Now = _fixedTime
        };

    private static string Compose(PromptContext context) => PromptComposer.Compose(context).Text;

    [Fact]
    public void Compose_IncludesCurrentDate()
    {
        Compose(Context()).ShouldContain("Today is Friday, 2026-05-15.");
    }

    // The date used to be the first line, which made the opening bytes of the system prompt change
    // every midnight and invalidated the whole cached prefix with them. It is the only dated
    // section, so it belongs after every static one: a day rollover then re-prefills the tail.
    [Fact]
    public void Compose_PutsTheDateAfterEveryStaticSection()
    {
        var result = Compose(Context(withSections: true));

        result.ShouldStartWith(BasePrompt.Instructions);
        var date = result.IndexOf("Today is", StringComparison.Ordinal);
        date.ShouldBeGreaterThan(result.IndexOf(BasePrompt.Instructions, StringComparison.Ordinal));
        date.ShouldBeGreaterThan(result.IndexOf("DOMAIN", StringComparison.Ordinal));
        date.ShouldBeGreaterThan(result.IndexOf("FS", StringComparison.Ordinal));
        date.ShouldBeGreaterThan(result.IndexOf("CLIENT", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_PlacesCustomInstructionsLast()
    {
        var result = Compose(Context(customInstructions: "CUSTOM", withSections: true));

        // Closest to the conversation, so they are the most recent guidance the model reads
        // rather than something buried above every tool prompt.
        result.ShouldEndWith("CUSTOM");
        result.IndexOf("CUSTOM", StringComparison.Ordinal)
            .ShouldBeGreaterThan(result.IndexOf("CLIENT", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_AppendsEverySectionItWasGiven()
    {
        var result = Compose(Context(customInstructions: "CUSTOM", withSections: true));

        result.ShouldContain("CUSTOM");
        result.ShouldContain("DOMAIN");
        result.ShouldContain("FS");
        result.ShouldContain("CLIENT");
        result.ShouldContain("Today is");
    }

    [Fact]
    public void Compose_IncludesAgentIdentity()
    {
        var result = Compose(Context(name: "Mycroft", description: "Voice assistant."));

        result.ShouldContain("## Identity");
        result.ShouldContain("You are Mycroft. Voice assistant.");
    }

    [Fact]
    public void Compose_PlacesIdentityAfterTheCoreDirectiveAndBeforeFeaturePrompts()
    {
        var result = Compose(Context(name: "Mycroft", description: "Voice assistant.", withSections: true));

        result.IndexOf(BasePrompt.Instructions, StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("## Identity", StringComparison.Ordinal));
        result.IndexOf("## Identity", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("DOMAIN", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_BlankName_OmitsIdentityFragment()
    {
        Compose(Context(name: "  ", description: "Voice assistant.")).ShouldNotContain("## Identity");
    }

    // The reply language is a hard output constraint, so it outranks even the custom instructions:
    // it is the last thing in the system prompt, closest to the conversation.
    [Fact]
    public void Compose_WithLanguage_PlacesTheLanguageSectionAfterCustomInstructions()
    {
        var result = Compose(Context(
            name: "Nabu", customInstructions: "CUSTOM", language: "es", withSections: true));

        result.ShouldEndWith(LanguagePrompt.Build("es")!);
        result.IndexOf("## Idioma", StringComparison.Ordinal)
            .ShouldBeGreaterThan(result.IndexOf("CUSTOM", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_NoLanguage_OmitsTheLanguageSection()
    {
        var result = Compose(Context(name: "Nabu", customInstructions: "CUSTOM", language: "   "));

        result.ShouldEndWith("CUSTOM");
        result.ShouldNotContain("## Idioma");
        result.ShouldNotContain("## Language");
    }

    // A section an agent selected by name sits between its custom instructions and the language
    // rule: it is more specific than what the deployment configured for the agent, and less
    // absolute than the language every other section contradicts by being written in English.
    [Fact]
    public void Compose_ASelectedSection_IsReadAfterCustomInstructionsAndBeforeTheLanguageRule()
    {
        var context = Context(name: "Nabu", customInstructions: "CUSTOM", language: "es") with
        {
            Selected = [PromptManifest.Selected(VoicePrompt.Name)!]
        };

        var result = Compose(context);

        result.IndexOf("VOICE RULES", StringComparison.Ordinal)
            .ShouldBeGreaterThan(result.IndexOf("CUSTOM", StringComparison.Ordinal));
        result.IndexOf("VOICE RULES", StringComparison.Ordinal)
            .ShouldBeLessThan(result.IndexOf("## Idioma", StringComparison.Ordinal));
    }
}