using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class IdentityPromptTests
{
    [Fact]
    public void Build_WithNameAndDescription_StatesIdentityWithRole()
    {
        IdentityPrompt.Build("Mycroft", "Voice-optimized assistant.")
            .ShouldBe("## Identity\n\nYou are Mycroft. Voice-optimized assistant.");
    }

    [Fact]
    public void Build_WhitespaceDescription_TreatedAsNoDescription()
    {
        IdentityPrompt.Build("Jonas", "   ")
            .ShouldBe("## Identity\n\nYou are Jonas.");
    }
}