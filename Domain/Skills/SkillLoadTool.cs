using Domain.Prompts;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Skills;

// The load tool as the conversation records it: its name, its one argument, how a loaded skill is
// recognised in a history and how a preload is written into one. The framework owns the tool
// (Microsoft.Agents.AI's `AgentSkillsProvider`), which Domain does not reference, so the names
// are spelled here and a test pins them to the framework's.
//
// A preload is written exactly as the framework's own load would have left it — an assistant
// message carrying the call, a tool message carrying the wrapped body — so a model sees one
// shape for a skill whoever loaded it, and "already loaded" is one question asked of the history
// with no second record to drift. Every deployed model was shown a pair it never emitted and took
// it as its own (`.scratch/jev-skill-preload/issues/01`).
public static class SkillLoadTool
{
    public const string Name = "load_skill";

    public const string SkillNameParameter = "skillName";

    public static IReadOnlySet<string> LoadedIn(IEnumerable<ChatMessage> history) =>
        history
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Where(c => string.Equals(c.Name, Name, StringComparison.Ordinal))
            .Select(SkillNameOf)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    // The two messages a load leaves: one call per skill on one assistant message, and one
    // result per call on one tool message. No reasoning content, because the model never reasoned.
    public static IReadOnlyList<ChatMessage> AsLoaded(IReadOnlyList<PromptSkill> skills, string callIdPrefix) =>
        AsPreloaded(new SkillPreload(SkillPreloadOutcome.Preloaded, skills), callIdPrefix);

    // The same two messages for a whole preload: the loads first, then the reads the skills
    // declared, on the same pair — the shape a model that loads and reads in one call leaves.
    // The read is the filesystem tool's own call, by its callable name and argument, with what
    // the tool answered, so nothing downstream can tell it from one the model made.
    public static IReadOnlyList<ChatMessage> AsPreloaded(SkillPreload preload, string callIdPrefix)
    {
        var loads = preload.Skills
            .Select((skill, i) => (
                Call: new FunctionCallContent(
                    $"{callIdPrefix}-{i + 1}", Name, new Dictionary<string, object?> { [SkillNameParameter] = skill.Name }),
                Result: (object)Wrapped(skill)))
            .ToList();
        var reads = preload.Reads
            .Select((read, i) => (
                Call: new FunctionCallContent(
                    $"{callIdPrefix}-read-{i + 1}",
                    ReadToolName,
                    new Dictionary<string, object?> { [VfsFileReadTool.FilePathParameter] = read.Path }),
                Result: (object)read.Result))
            .ToList();
        var pairs = loads.Concat(reads).ToList();

        return
        [
            new ChatMessage(ChatRole.Assistant, [.. pairs.Select(p => p.Call)]),
            new ChatMessage(ChatRole.Tool, [.. pairs.Select(p => new FunctionResultContent(p.Call.CallId, p.Result))])
        ];
    }

    public static readonly string ReadToolName = FileSystemToolFeature.Callable(VfsFileReadTool.Name);

    // The framework's wrapper, captured off a real load on Microsoft.Agents.AI 1.20.0 and pinned
    // by a test against one. The empty resource and script elements are there because no skill
    // here ships either (docs/adr/0039).
    public static string Wrapped(PromptSkill skill) =>
        $"<name>{skill.Name}</name>\n" +
        $"<description>{skill.Description}</description>\n" +
        "\n" +
        "<instructions>\n" +
        $"{skill.Body}\n" +
        "</instructions>\n" +
        "\n" +
        "<available_resources />\n" +
        "\n" +
        "<available_scripts />";

    private static string? SkillNameOf(FunctionCallContent call) =>
        call.Arguments is not null && call.Arguments.TryGetValue(SkillNameParameter, out var value)
            ? value?.ToString()
            : null;
}