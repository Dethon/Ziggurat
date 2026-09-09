using System.ComponentModel;
using System.Reflection;
using Domain.Tools.Memory;
using Shouldly;

namespace Tests.Unit.Domain.Memory;

// A parameter the model cannot see the shape of is a parameter it invents. memory_forget takes
// both `memoryId` and `memoryIds` and described neither, so glm-5.3-flash JSON-encoded an array
// of memory *text* into the singular field — `["Busca piso de alquiler en Chamberí"]` — and
// deleted nothing. The names alone do not say that an id comes from a search result rather than
// from the memory's words.
public class MemoryToolParameterDescriptionTests
{
    public static TheoryData<string> ForgetParameters =>
        [.. typeof(MemoryForgetTool)
            .GetMethod(nameof(MemoryForgetTool.Run))!
            .GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Select(p => p.Name!)];

    [Theory]
    [MemberData(nameof(ForgetParameters))]
    public void EveryForgetParameter_TellsTheModelWhatToPass(string name)
    {
        var parameter = typeof(MemoryForgetTool)
            .GetMethod(nameof(MemoryForgetTool.Run))!
            .GetParameters()
            .Single(p => p.Name == name);

        parameter.GetCustomAttribute<DescriptionAttribute>()
            .ShouldNotBeNull($"memory_forget's '{name}' reaches the model with no description, " +
                             "so its shape is a guess");
    }

    // Every parameter is re-sent on every request of every conversation, and the four filters
    // (categories, tags, olderThan, maxImportance) were dreaming-time knobs the model never used:
    // a sweep names the ids the recall block shows, importance included. What the tool takes is
    // what a forget needs and nothing that widens its schema for a case nobody has.
    [Fact]
    public void Forget_TakesOnlyWhatAForgetNeeds()
    {
        typeof(MemoryForgetTool)
            .GetMethod(nameof(MemoryForgetTool.Run))!
            .GetParameters()
            .Where(p => p.ParameterType != typeof(CancellationToken))
            .Select(p => p.Name)
            .ShouldBe(["memoryId", "memoryIds", "query", "reason"]);
    }
}