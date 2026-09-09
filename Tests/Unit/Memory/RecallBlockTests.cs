using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Memory;
using Domain.Prompts;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Memory;

public class RecallBlockTests
{
    private const string Marker = "[Memory context]";

    // The importance is interpolated with the same format the block uses, so the expected
    // strings pin the shape and the format specifier without pinning a decimal separator.
    private static MemorySearchResult Memory(string content, MemoryCategory category, double importance) =>
        new(new MemoryEntry
        {
            Id = "m1",
            UserId = "u1",
            Category = category,
            Content = content,
            Importance = importance,
            Confidence = 0.9,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastAccessedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)
        }, 0.95);

    // The block is the only place a memory reaches the model, and memory_forget asks for the id
    // "exactly as spelled". Without the id in the block there was nothing to spell, and a model
    // that reached for the by-id path invented one.
    [Fact]
    public void Render_CarriesEachMemorysId_SoAForgetCanNameIt()
    {
        var context = new MemoryContext([Memory("prefers tea over coffee", MemoryCategory.Preference, 0.9)], null);

        var block = RecallBlock.Render(context);

        block.ShouldContain("- [m1] prefers tea over coffee (preference, importance: ");
    }

    [Fact]
    public void Render_WithMemoriesAndNoProfile_ListsEachMemory()
    {
        var context = new MemoryContext(
        [
            Memory("prefers tea over coffee", MemoryCategory.Preference, 0.9),
            Memory("works at Contoso", MemoryCategory.Fact, 0.8)
        ], null);

        var block = RecallBlock.Render(context);

        block.ShouldBe(string.Join(Environment.NewLine,
            "[Memory context]",
            $"- [m1] prefers tea over coffee (preference, importance: {0.9:F1})",
            $"- [m1] works at Contoso (fact, importance: {0.8:F1})",
            "[End memory context]") + Environment.NewLine);
    }

    [Fact]
    public void Render_WithProfile_AppendsTheProfileLine()
    {
        var context = new MemoryContext(
            [Memory("prefers tea over coffee", MemoryCategory.Preference, 0.9)],
            new PersonalityProfile
            {
                UserId = "u1",
                Summary = "Brief communicator",
                LastUpdated = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)
            });

        var block = RecallBlock.Render(context);

        block.ShouldBe(string.Join(Environment.NewLine,
            "[Memory context]",
            $"- [m1] prefers tea over coffee (preference, importance: {0.9:F1})",
            "[User profile: Brief communicator]",
            "[End memory context]") + Environment.NewLine);
    }

    [Fact]
    public void Render_UsesTheMarkerTheMemorySystemPromptNames()
    {
        // The agent's system prompt tells the model to look for this block by name. Renaming
        // one side and not the other leaves the model hunting for a block that never arrives.
        var block = RecallBlock.Render(new MemoryContext([], null));

        block.ShouldContain(Marker);
        MemoryPrompts.FeatureSystemPrompt.ShouldContain(Marker);
    }

    [Fact]
    public void Render_AfterAJsonRoundTrip_IsByteIdenticalToRenderingTheOriginal()
    {
        // Every request re-renders a block for each historical user turn that carries context,
        // and those contexts come back from Redis as deserialized values. Any drift between the
        // two renderings rewrites the prompt prefix on every turn and costs the prompt cache.
        var context = new MemoryContext(
            [Memory("prefers tea over coffee", MemoryCategory.Preference, 0.9)],
            new PersonalityProfile
            {
                UserId = "u1",
                Summary = "Brief communicator",
                LastUpdated = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero)
            });

        var message = new ChatMessage(ChatRole.User, "Hello");
        message.SetMemoryContext(context);
        var reloaded = JsonSerializer.Deserialize<ChatMessage>(JsonSerializer.Serialize(message));

        var roundTripped = reloaded!.GetMemoryContext();
        roundTripped.ShouldNotBeNull();
        RecallBlock.Render(roundTripped).ShouldBe(RecallBlock.Render(context));
    }
}